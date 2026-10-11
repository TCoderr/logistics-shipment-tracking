namespace Lst.Platform.Realtime;

/// <summary>
/// Canlı bağlantıların oturumla birlikte sonlandırılması. Kimlik modülü çıkışta çağırır; gerçek uygulama host'tadır (SignalR).
/// Varsayılan uygulama hiçbir şey yapmaz (canlı kanal yokken).
/// </summary>
public interface IRealtimeSessions
{
    /// <summary>Kullanıcının açık tüm canlı bağlantılarını kapatır. Kapatılan bağlantı sayısını döner.</summary>
    int DisconnectUser(Guid userId);
}

public sealed class NullRealtimeSessions : IRealtimeSessions
{
    public int DisconnectUser(Guid userId) => 0;
}
