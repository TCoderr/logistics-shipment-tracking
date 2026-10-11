using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Lst.Api.Realtime;
using Lst.IntegrationTests.Infrastructure;
using Lst.Platform.Outbox;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Lst.IntegrationTests;

/// <summary>
/// Dilim 10: kimlikli SignalR hub'ı, sunucu yönetimli abonelik yetkisi, mevcut bağlantının iptali (rol/hub ataması, çıkış, bilet süresi),
/// outbox'tan PII'siz geçersizleme ve gönderim hatasının commit'i etkilememesi. Gerçek PostgreSQL; taşıma TestServer üzerinden long polling.
/// </summary>
public sealed class RealtimeTests(LstDatabaseFixture db) : IAsyncDisposable
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromMilliseconds(700);
    private readonly List<IDisposable> _disposables = [];
    private readonly List<HubConnection> _connections = [];
    private DateTimeOffset _clock = DateTimeOffset.UtcNow.AddSeconds(1);

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private DateTimeOffset Next() => _clock = _clock.AddSeconds(10);

    private LstApiFactory Factory(TimeProvider? time = null, Action<IServiceCollection>? services = null)
    {
        var factory = new LstApiFactory(db, time, services);
        _disposables.Add(factory);
        return factory;
    }

    private static readonly Uri Origin = new("https://localhost");

    private sealed record Client(
        HubConnection Connection, HttpClient Http, CookieContainer Cookies, Channel<(string Name, JsonElement Payload)> Messages, TaskCompletionSource Closed);

    private async Task<(HttpClient Http, CookieContainer Cookies)> LoginAsync(LstApiFactory factory, string user)
    {
        var cookies = new CookieContainer();
        var http = factory.CreateDefaultClient(Origin, new CookieContainerHandler(cookies));
        await TestApi.LoginAsync(http, db.User(user));
        return (http, cookies);
    }

    private static HubConnection Build(LstApiFactory factory, string? cookieHeader, string? origin = null) =>
        new HubConnectionBuilder()
            .WithUrl("https://localhost" + OperationsRealtimeHub.Path, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (cookieHeader is not null)
                {
                    options.Headers["Cookie"] = cookieHeader;
                }

                if (origin is not null)
                {
                    options.Headers["Origin"] = origin;
                }
            })
            .Build();

    private async Task<Client> ConnectAsync(LstApiFactory factory, string user)
    {
        var (http, cookies) = await LoginAsync(factory, user);
        var connection = Build(factory, cookies.GetCookieHeader(Origin));
        _connections.Add(connection);
        var messages = Channel.CreateUnbounded<(string, JsonElement)>();
        foreach (var name in new[] { "ShipmentChanged", "ExceptionChanged", "SubscriptionRevoked" })
        {
            connection.On<JsonElement>(name, payload => messages.Writer.TryWrite((name, payload)));
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await connection.StartAsync(TestApi.Ct);
        return new Client(connection, http, cookies, messages, closed);
    }

    private static async Task<(string Name, JsonElement Payload)> ReceiveAsync(Client client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestApi.Ct);
        timeout.CancelAfter(ReceiveTimeout);
        return await client.Messages.Reader.ReadAsync(timeout.Token);
    }

    private static async Task AssertSilentAsync(Client client)
    {
        await Task.Delay(SilenceWindow, TestApi.Ct);
        Assert.False(client.Messages.Reader.TryRead(out var unexpected), $"Beklenmeyen mesaj: {unexpected.Name}");
    }

    /// <summary>
    /// Sunucunun fırlattığı HubException kodu. İstemciye giden metin sabit bir önek + "HubException: kod" biçimindedir
    /// (ayrıntılı hata kapalıdır; iç istisna metni asla gönderilmez).
    /// </summary>
    private static async Task<string> DenialAsync(Func<Task> call)
    {
        var message = (await Assert.ThrowsAsync<HubException>(call)).Message;
        const string marker = "HubException: ";
        var index = message.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Beklenmeyen hub hatası: " + message);
        return message[(index + marker.Length)..];
    }

    private async Task<List<Guid>> PendingMessagesAsync(Guid shipmentId)
    {
        await using var connection = db.OpenOwnerConnection();
        await using var command = new NpgsqlCommand(
            """
            SELECT message_id FROM platform.outbox_messages
             WHERE payload->>'shipmentId' = @id AND processed_at IS NULL AND dead_lettered_at IS NULL
               AND type <> 'notification.recipient-update.v1'
             ORDER BY id
            """, connection);
        command.Parameters.AddWithValue("id", shipmentId.ToString());
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(TestApi.Ct);
        while (await reader.ReadAsync(TestApi.Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Outbox dağıtıcısını yalnız bu gönderinin bekleyen mesajları için çalıştırır (worker testlerde kapalıdır).</summary>
    private async Task<IReadOnlyList<OutboxResult>> DispatchAsync(LstApiFactory factory, Guid shipmentId)
    {
        var ids = await PendingMessagesAsync(shipmentId);
        var dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();
        var results = new List<OutboxResult>();
        foreach (var claimed in await dispatcher.ClaimAsync(50, ids, TestApi.Ct))
        {
            results.Add(await dispatcher.ProcessAsync(claimed, TestApi.Ct));
        }

        return results;
    }

    private async Task ExecuteOwnerAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = db.OpenOwnerConnection();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestApi.Ct);
    }

    [Fact]
    public async Task Anonymous_and_cross_origin_clients_cannot_connect()
    {
        var factory = Factory();
        using var anonymousHttp = factory.CreateHttpsClient();
        using (var negotiate = await anonymousHttp.PostAsync(OperationsRealtimeHub.Path + "/negotiate?negotiateVersion=1", content: null, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
            Assert.Equal("unauthenticated", await TestApi.ProblemCodeAsync(negotiate));
        }

        await using (var anonymous = Build(factory, cookieHeader: null))
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => anonymous.StartAsync(TestApi.Ct));
        }

        // Geçerli oturum cookie'si olsa bile başka origin'den gelen el sıkışması reddedilir.
        var (http, cookies) = await LoginAsync(factory, "ops.agent");
        var cookieHeader = cookies.GetCookieHeader(Origin);
        using (http)
        {
            await using var foreign = Build(factory, cookieHeader, origin: "https://evil.example");
            var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => foreign.StartAsync(TestApi.Ct));
            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);

            await using var sameOrigin = Build(factory, cookieHeader, origin: "https://localhost");
            await sameOrigin.StartAsync(TestApi.Ct);
            Assert.Equal(HubConnectionState.Connected, sameOrigin.State);
        }
    }

    [Fact]
    public async Task Subscriptions_are_authorized_by_current_role_hub_assignment_and_shipment_scope()
    {
        var factory = Factory();
        var ops = await ConnectAsync(factory, "ops.agent");
        var hubIzm = await ConnectAsync(factory, "hub.izm");
        var (shipmentId, _, _) = await TestApi.CreateAsync(ops.Http, new ShipmentJson());   // IST-01 → ANK-02

        await ops.Connection.InvokeAsync("SubscribeOperations", TestApi.Ct);
        await ops.Connection.InvokeAsync("SubscribeShipment", shipmentId, TestApi.Ct);
        // Operasyon kullanıcısının hub ataması yoktur: hub aboneliği yalnız atanmış kullanıcıya açıktır.
        Assert.Equal("forbidden", await DenialAsync(() => ops.Connection.InvokeAsync("SubscribeHub", "IST-01", TestApi.Ct)));

        Assert.Equal("forbidden", await DenialAsync(() => hubIzm.Connection.InvokeAsync("SubscribeOperations", TestApi.Ct)));
        await hubIzm.Connection.InvokeAsync("SubscribeHub", "IZM-01", TestApi.Ct);
        foreach (var hubCode in new[] { "IST-01", "ZZZ-99", "ist-01", "IZM-01'; --", string.Empty })
        {
            Assert.Equal("forbidden", await DenialAsync(() => hubIzm.Connection.InvokeAsync("SubscribeHub", hubCode, TestApi.Ct)));
        }

        // Kapsam dışı gönderi ile var olmayan gönderi ayırt edilemez.
        var outOfScope = await DenialAsync(() => hubIzm.Connection.InvokeAsync("SubscribeShipment", shipmentId, TestApi.Ct));
        var unknown = await DenialAsync(() => hubIzm.Connection.InvokeAsync("SubscribeShipment", Guid.CreateVersion7(), TestApi.Ct));
        Assert.Equal("not-found", outOfScope);
        Assert.Equal(outOfScope, unknown);

        // İstemci grup adı veremez: hub'da yalnız bu dört metot vardır.
        var missing = await Assert.ThrowsAsync<HubException>(() => hubIzm.Connection.InvokeAsync("AddToGroup", "ops", TestApi.Ct));
        Assert.Contains("Method does not exist", missing.Message, StringComparison.Ordinal);

        // Yetkisiz denemelerin hiçbiri abonelik bırakmadı: gönderi değişikliği hub.izm'e ulaşmaz, ops'a ulaşır.
        Assert.All(await DispatchAsync(factory, shipmentId), result => Assert.Equal(OutboxResult.Processed, result));
        var received = await ReceiveAsync(ops);
        Assert.Equal("ShipmentChanged", received.Name);
        Assert.Equal(shipmentId, received.Payload.GetProperty("shipmentId").GetGuid());
        await AssertSilentAsync(hubIzm);
    }

    [Fact]
    public async Task Invalidations_come_from_the_outbox_are_pii_free_and_reach_only_authorized_groups()
    {
        var factory = Factory();
        var ops = await ConnectAsync(factory, "ops.agent");
        var hubIst = await ConnectAsync(factory, "hub.ist");
        var hubAnk = await ConnectAsync(factory, "hub.ank");
        var hubIzm = await ConnectAsync(factory, "hub.izm");
        await ops.Connection.InvokeAsync("SubscribeOperations", TestApi.Ct);
        await hubIst.Connection.InvokeAsync("SubscribeHub", "IST-01", TestApi.Ct);
        await hubAnk.Connection.InvokeAsync("SubscribeHub", "ANK-02", TestApi.Ct);
        await hubIzm.Connection.InvokeAsync("SubscribeHub", "IZM-01", TestApi.Ct);

        var json = new ShipmentJson();
        var (shipmentId, labels, _) = await TestApi.CreateAsync(ops.Http, json);

        // Commit olmuş ama henüz dağıtılmamış değişiklik: hiçbir istemci mesaj almaz (yayın commit'e bağlıdır, tersi değil).
        await AssertSilentAsync(ops);
        Assert.All(await DispatchAsync(factory, shipmentId), result => Assert.Equal(OutboxResult.Processed, result));

        foreach (var client in new[] { ops, hubIst, hubAnk })
        {
            var (name, payload) = await ReceiveAsync(client);
            Assert.Equal("ShipmentChanged", name);
            // Yük yalnız kimlik + durum: alan kümesi sabittir.
            Assert.Equal(["shipmentId", "status"], payload.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(shipmentId, payload.GetProperty("shipmentId").GetGuid());
            Assert.Equal("Created", payload.GetProperty("status").GetString());
            var raw = payload.GetRawText();
            Assert.All(json.PiiValues, pii => Assert.DoesNotContain(pii, raw, StringComparison.Ordinal));
        }

        await AssertSilentAsync(hubIzm);

        // Tek gönderi aboneliği: hub.ist kendi kapsamındaki gönderiye abone olur; durum değişince hem hub hem gönderi grubundan
        // (yinelenen, zararsız) ipucu alabilir.
        await hubIst.Connection.InvokeAsync("SubscribeShipment", shipmentId, TestApi.Ct);
        using (var scan = await TestApi.ScanAsync(hubIst.Http, "IST-01", labels[0], "Arrival", Next()))
        {
            Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        }

        Assert.All(await DispatchAsync(factory, shipmentId), result => Assert.Equal(OutboxResult.Processed, result));
        Assert.Equal("AtHub", (await ReceiveAsync(ops)).Payload.GetProperty("status").GetString());
        Assert.Equal("AtHub", (await ReceiveAsync(hubIst)).Payload.GetProperty("status").GetString());
        Assert.Equal("AtHub", (await ReceiveAsync(hubAnk)).Payload.GetProperty("status").GetString());
        await AssertSilentAsync(hubIzm);

        // Abonelikten çıkınca gönderi grubundan artık mesaj gelmez (hub grubu sürer).
        await hubIst.Connection.InvokeAsync("UnsubscribeShipment", shipmentId, TestApi.Ct);
    }

    [Fact]
    public async Task Exception_changes_are_published_with_ids_only_including_assignment()
    {
        var factory = Factory();
        var ops = await ConnectAsync(factory, "ops.queue");
        var hubIst = await ConnectAsync(factory, "hub.ist");
        await ops.Connection.InvokeAsync("SubscribeOperations", TestApi.Ct);
        var (shipmentId, labels, _) = await TestApi.CreateAsync(ops.Http, new ShipmentJson(packageCount: 2));
        Assert.All(await DispatchAsync(factory, shipmentId), result => Assert.Equal(OutboxResult.Processed, result));
        Assert.Equal("ShipmentChanged", (await ReceiveAsync(ops)).Name);

        // Konsolidasyon reddi PackageSplit istisnası açar.
        using (var arrival = await TestApi.ScanAsync(hubIst.Http, "IST-01", labels[0], "Arrival", Next()))
        {
            Assert.Equal(HttpStatusCode.OK, arrival.StatusCode);
        }

        using (var departure = await TestApi.ScanAsync(hubIst.Http, "IST-01", labels[0], "Departure", Next()))
        {
            Assert.Equal(HttpStatusCode.Conflict, departure.StatusCode);
        }

        Assert.All(await DispatchAsync(factory, shipmentId), result => Assert.Equal(OutboxResult.Processed, result));
        var messages = new List<(string Name, JsonElement Payload)> { await ReceiveAsync(ops), await ReceiveAsync(ops) };
        var opened = Assert.Single(messages, m => m.Name == "ExceptionChanged").Payload;
        Assert.Equal(["exceptionId", "shipmentId", "status"], opened.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("Open", shipmentId), (opened.GetProperty("status").GetString(), opened.GetProperty("shipmentId").GetGuid()));
        var exceptionId = opened.GetProperty("exceptionId").GetGuid();

        // Atama da kuyruğu izleyenlere bildirilir (atanan kullanıcı kimliği yükte yoktur).
        using var list = await ops.Http.GetAsync($"/api/exceptions?shipmentId={shipmentId}", TestApi.Ct);
        var item = Assert.Single((await TestApi.JsonAsync(list)).GetProperty("items").EnumerateArray());
        var me = await db.UserIdAsync("ops.queue");
        using (var assign = await TestApi.PostCommandAsync(ops.Http, $"/api/exceptions/{exceptionId}/assign", new { assigneeUserId = me }, item.GetProperty("etag").GetString()))
        {
            Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        }

        Assert.All(await DispatchAsync(factory, shipmentId), result => Assert.Equal(OutboxResult.Processed, result));
        var assigned = await ReceiveAsync(ops);
        Assert.Equal("ExceptionChanged", assigned.Name);
        Assert.Equal("Assigned", assigned.Payload.GetProperty("status").GetString());
        Assert.DoesNotContain(me.ToString(), assigned.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Revalidation_revokes_existing_subscriptions_when_hub_assignment_or_role_is_removed()
    {
        var factory = Factory();
        var ops = await ConnectAsync(factory, "ops.agent");
        var hub = await ConnectAsync(factory, "rt.hub");            // IZM-01
        var revokedOps = await ConnectAsync(factory, "rt.ops");
        await hub.Connection.InvokeAsync("SubscribeHub", "IZM-01", TestApi.Ct);
        await revokedOps.Connection.InvokeAsync("SubscribeOperations", TestApi.Ct);
        var revalidator = factory.Services.GetRequiredService<RealtimeRevalidator>();

        // Değişiklik yokken yeniden doğrulama kimseye dokunmaz.
        var unchanged = await revalidator.RunOnceAsync(TestApi.Ct);
        Assert.Equal((0, 0), (unchanged.ClosedConnections, unchanged.RevokedSubscriptions));

        // 1) Hub ataması kaldırılır: bağlantı açık kalır, abonelik iptal edilir ve istemciye bildirilir.
        await ExecuteOwnerAsync("DELETE FROM identity.user_hub_assignments WHERE user_id = @id", ("id", await db.UserIdAsync("rt.hub")));
        // 2) Operasyon rolü kaldırılır: kullanıcının hiç rolü kalmaz → bağlantı kapatılır.
        await db.RemoveRoleAsync("rt.ops", "OpsAgent");

        var result = await revalidator.RunOnceAsync(TestApi.Ct);
        Assert.Equal((1, 1), (result.ClosedConnections, result.RevokedSubscriptions));

        var revoked = await ReceiveAsync(hub);
        Assert.Equal("SubscriptionRevoked", revoked.Name);
        Assert.Equal(("Hub", "IZM-01"), (revoked.Payload.GetProperty("kind").GetString(), revoked.Payload.GetProperty("key").GetString()));
        Assert.Equal(HubConnectionState.Connected, hub.Connection.State);
        await revokedOps.Closed.Task.WaitAsync(ReceiveTimeout, TestApi.Ct);

        // İptalden sonra o hub'ın gönderi değişiklikleri eski aboneye ulaşmaz; yetkili kullanıcıya ulaşır.
        await ops.Connection.InvokeAsync("SubscribeOperations", TestApi.Ct);
        var (shipmentId, _, _) = await TestApi.CreateAsync(ops.Http, new ShipmentJson(origin: "IZM-01"));
        Assert.All(await DispatchAsync(factory, shipmentId), r => Assert.Equal(OutboxResult.Processed, r));
        Assert.Equal(shipmentId, (await ReceiveAsync(ops)).Payload.GetProperty("shipmentId").GetGuid());
        await AssertSilentAsync(hub);

        // Yeniden abone olma da güncel atamaya göre reddedilir; rolü kalmayan kullanıcı yeniden bağlanamaz.
        Assert.Equal("forbidden", await DenialAsync(() => hub.Connection.InvokeAsync("SubscribeHub", "IZM-01", TestApi.Ct)));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => revokedOps.Connection.StartAsync(TestApi.Ct));
    }

    [Fact]
    public async Task Logout_closes_the_users_connections_and_ticket_expiry_closes_stale_ones()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = Factory(time);
        var leaving = await ConnectAsync(factory, "rt.logout");
        var staying = await ConnectAsync(factory, "ops.agent");
        var expiring = await ConnectAsync(factory, "rt.expiry");
        await leaving.Connection.InvokeAsync("SubscribeOperations", TestApi.Ct);
        await staying.Connection.InvokeAsync("SubscribeOperations", TestApi.Ct);

        // Çıkış: cookie silinse de açık kalacak olan bağlantı sunucu tarafından kapatılır; başka kullanıcılar etkilenmez.
        using (var logout = await leaving.Http.PostAsync("/api/auth/logout", content: null, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        await leaving.Closed.Task.WaitAsync(ReceiveTimeout, TestApi.Ct);
        Assert.Equal(HubConnectionState.Connected, staying.Connection.State);
        // Sunucu kaydı bağlantı kapanınca temizlenir (kalan: staying + expiring).
        var registry = factory.Services.GetRequiredService<RealtimeConnectionRegistry>();
        for (var i = 0; i < 100 && registry.Count != 2; i++)
        {
            await Task.Delay(50, TestApi.Ct);
        }

        Assert.Equal(2, registry.Count);
        // Çıkış oturum cookie'sini tarayıcıdan siler: aynı tarayıcı (cookie kavanozu) artık kimliksizdir ve yeniden bağlanamaz.
        Assert.DoesNotContain("lst.auth=", leaving.Cookies.GetCookieHeader(Origin), StringComparison.Ordinal);
        await using (var afterLogout = Build(factory, leaving.Cookies.GetCookieHeader(Origin)))
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => afterLogout.StartAsync(TestApi.Ct));
        }

        // Bilet süresi (8 saat) dolmadan yeniden doğrulama bağlantıyı kapatmaz; dolunca kapatır.
        var revalidator = factory.Services.GetRequiredService<RealtimeRevalidator>();
        time.Advance(TimeSpan.FromHours(7));
        Assert.Equal(0, (await revalidator.RunOnceAsync(TestApi.Ct)).ClosedConnections);
        Assert.Equal(HubConnectionState.Connected, expiring.Connection.State);

        time.Advance(TimeSpan.FromHours(2));
        var result = await revalidator.RunOnceAsync(TestApi.Ct);
        Assert.Equal(2, result.ClosedConnections);   // rt.expiry ve ops.agent (ikisinin de bileti doldu)
        await expiring.Closed.Task.WaitAsync(ReceiveTimeout, TestApi.Ct);
        await staying.Closed.Task.WaitAsync(ReceiveTimeout, TestApi.Ct);
    }

    [Fact]
    public async Task Publish_failure_never_affects_the_command_and_is_retried_then_replayed_harmlessly()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var failing = new SwitchableNotifier();
        var factory = Factory(time, services =>
        {
            services.AddSingleton(failing);
            services.Replace(ServiceDescriptor.Singleton<IRealtimeNotifier>(sp =>
                new FailingNotifier(sp.GetRequiredService<SwitchableNotifier>(), ActivatorUtilities.CreateInstance<SignalRRealtimeNotifier>(sp))));
        });
        var ops = await ConnectAsync(factory, "ops.agent");
        await ops.Connection.InvokeAsync("SubscribeOperations", TestApi.Ct);

        // Yayın bozukken komut yine başarılıdır ve veri okunabilir (commit yayına bağlı değildir).
        failing.Fail = true;
        var (shipmentId, _, _) = await TestApi.CreateAsync(ops.Http, new ShipmentJson());
        var (detail, _) = await TestApi.GetDetailAsync(ops.Http, shipmentId);
        Assert.Equal("Created", detail.GetProperty("status").GetString());

        var messageId = Assert.Single(await PendingMessagesAsync(shipmentId));
        var dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();
        Assert.Equal(OutboxResult.RetryScheduled, await dispatcher.ProcessAsync(Assert.Single(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct)), TestApi.Ct));
        await AssertSilentAsync(ops);
        // Backoff dolmadan yeniden alınmaz; mesaj işlenmiş sayılmaz.
        Assert.Empty(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct));

        // Yayın düzelir: worker yeniden başlamış gibi aynı mesaj yeniden denenir ve teslim edilir.
        failing.Fail = false;
        time.Advance(TimeSpan.FromMinutes(1));
        var retry = Assert.Single(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct));
        Assert.True(await dispatcher.RunHandlersAsync(retry, TestApi.Ct));
        Assert.Equal(shipmentId, (await ReceiveAsync(ops)).Payload.GetProperty("shipmentId").GetGuid());

        // Handler çalıştı ama "işlendi" yazılamadan süreç çöktü: lease dolunca mesaj YENİDEN yayınlanır. Yinelenen ipucu zararsızdır:
        // yük aynıdır, sunucuda hiçbir durum değişmez, istemci yalnız kaynağı yeniden okur.
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(OutboxResult.Processed, await dispatcher.ProcessAsync(Assert.Single(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct)), TestApi.Ct));
        var duplicate = await ReceiveAsync(ops);
        Assert.Equal(("ShipmentChanged", shipmentId), (duplicate.Name, duplicate.Payload.GetProperty("shipmentId").GetGuid()));
        Assert.Empty(await PendingMessagesAsync(shipmentId));
        var (after, _) = await TestApi.GetDetailAsync(ops.Http, shipmentId);
        Assert.Equal("Created", after.GetProperty("status").GetString());
        Assert.Single(after.GetProperty("timeline").EnumerateArray());
    }

    private sealed class SwitchableNotifier
    {
        public volatile bool Fail;
    }

    private sealed class FailingNotifier(SwitchableNotifier state, IRealtimeNotifier inner) : IRealtimeNotifier
    {
        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken) =>
            state.Fail ? throw new InvalidOperationException("simulated realtime transport failure") : inner.PublishAsync(envelope, cancellationToken);
    }
}
