namespace Lst.Modules.Identity;

public static class Roles
{
    public const string OpsAgent = "OpsAgent";
    public const string OpsManager = "OpsManager";
    public const string HubOperator = "HubOperator";

    public static readonly IReadOnlyList<string> All = [OpsAgent, OpsManager, HubOperator];
}

public static class Policies
{
    /// <summary>Gönderi oluşturma: OpsAgent veya OpsManager (güncel DB rolü).</summary>
    public const string CanCreateShipment = "CanCreateShipment";

    /// <summary>Gönderi okuma: herhangi bir iç rol (güncel DB rolü); kaynak kapsamı handler'da ayrıca denetlenir.</summary>
    public const string InternalUser = "InternalUser";

    /// <summary>Operasyon personeli (OpsAgent veya OpsManager; güncel DB rolü): iptal, iade, istisna işlemleri.</summary>
    public const string OperationsStaff = "OperationsStaff";

    /// <summary>Hub taraması: HubOperator rolü (güncel DB); hub ataması handler'da ayrıca denetlenir.</summary>
    public const string HubScanner = "HubScanner";

    /// <summary>Yalnız OpsManager (güncel DB rolü): simülatör, outbox replay, public kod rotasyonu, audit okuma.</summary>
    public const string OpsManagerOnly = "OpsManagerOnly";

    /// <summary>Yalnız oturum açmış olmak (ör. /api/auth/me, logout).</summary>
    public const string Authenticated = "Authenticated";
}
