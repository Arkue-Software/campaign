// RedVital — campaign-service
// Quién hace la petición, leído del token ya validado por este servicio
// (EC-03: Campañas revalida, no confía en que el gateway lo hizo).
// Lo usan T-312.2, T-312.3 y T-312.4.

namespace RedVital.Campanas.Dominio;

public sealed record Solicitante(string? Sub, string? Rol, string? Jurisdiccion)
{
    public static readonly Solicitante Anonimo = new(null, null, null);

    public bool EsAnonimo => Rol is null;
    public bool EsDonante => Rol == "donante";
    public bool EsAdminBanco => Rol == "admin_banco";
    public bool EsInstitucional => Rol is "operador" or "admin_banco" or "coordinador" or "admin_nacional";

    public Guid? InstitucionJurisdiccion =>
        Jurisdiccion is not null && Jurisdiccion.StartsWith("institucion:") &&
        Guid.TryParse(Jurisdiccion["institucion:".Length..], out var id) ? id : null;

    public string? RutaJurisdiccion =>
        Jurisdiccion is not null && Jurisdiccion.StartsWith("territorio:") ? Jurisdiccion["territorio:".Length..] : null;

    /// <summary>
    /// Regla de jurisdicción a nivel de fila (EC-01). Una ruta territorial
    /// cubre a sus descendientes: "/00/11" alcanza a "/00/11/11001", pero
    /// "/00/1" NO alcanza a "/00/11" (por eso se compara con "/" al final).
    /// </summary>
    public bool Alcanza(Campania c)
    {
        if (InstitucionJurisdiccion is { } inst) return c.InstitucionId == inst;
        if (RutaJurisdiccion is { } ruta) return c.TerritorioRuta == ruta || c.TerritorioRuta.StartsWith(ruta + "/");
        return false;
    }
}
