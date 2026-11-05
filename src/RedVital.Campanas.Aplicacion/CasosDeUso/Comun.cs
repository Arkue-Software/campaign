// RedVital — campaign-service. Tipos comunes de T-312.2, T-312.3 y T-312.4.

using RedVital.Campanas.Dominio;

namespace RedVital.Campanas.Aplicacion.CasosDeUso;

public enum Fallo { Ninguno, NoPermitido, NoEncontrado, Conflicto, ReglaNegocio, Invalido, NoDisponible }

public record Resultado<T>(T? Valor, Fallo Fallo = Fallo.Ninguno, string? Detalle = null)
{
    public bool Exitoso => Fallo == Fallo.Ninguno;
    public static Resultado<T> Ok(T valor) => new(valor);
    public static Resultado<T> Error(Fallo fallo, string? detalle = null) => new(default, fallo, detalle);
}

/// <summary>
/// Forma pública de una campaña (CampaniaResponse del contrato). Expone
/// cupo_disponible y NUNCA cupo_reservado. donaciones_registradas solo se
/// entrega a los roles institucionales (DD 15.3); para el público es nulo.
/// institucion_nombre sale de la copia local de instituciones (EV-07); es nulo
/// mientras el evento de esa institución no haya llegado.
/// </summary>
public record CampaniaRespuesta(
    Guid Id, Guid InstitucionId, string? InstitucionNombre, string TerritorioCodigo, string TerritorioRuta,
    string Nombre, string? Descripcion, string Sede,
    DateTimeOffset IniciaEn, DateTimeOffset TerminaEn, string Estado,
    int? CupoTotal, int? CupoDisponible, DateTimeOffset? PublicadaEn, int? DonacionesRegistradas)
{
    public static CampaniaRespuesta Desde(Campania c, string? institucionNombre, bool institucional = false) => new(
        c.Id, c.InstitucionId, institucionNombre, c.TerritorioCodigo, c.TerritorioRuta, c.Nombre, c.Descripcion, c.Sede,
        c.IniciaEn, c.TerminaEn, c.Estado, c.CupoTotal, c.CupoDisponible, c.PublicadaEn,
        institucional ? c.DonacionesRegistradas : null);
}

public static class NombresInstitucion
{
    public static async Task<string?> DeAsync(IResolutorInstitucion instituciones, Guid institucionId, CancellationToken ct) =>
        (await instituciones.NombresAsync([institucionId], ct)).GetValueOrDefault(institucionId);
}

public static class Auditar
{
    public static RegistroAuditoriaCamp Hecho(Solicitante s, string operacion, Guid? recursoId, string resultado,
        string correlacionId, string? jurisdiccionSolicitada = null) => new()
    {
        Id = Guid.NewGuid(),
        ActorTipo = s.EsAnonimo ? "anonimo" : s.Rol == "servicio" ? "sistema" : "usuario",
        ActorId = s.Sub,
        Rol = s.Rol,
        JurisdiccionSolicitada = jurisdiccionSolicitada,
        Operacion = operacion,
        RecursoTipo = "campania",
        RecursoId = recursoId,
        Resultado = resultado,
        CorrelacionId = correlacionId,
        OcurridoEn = DateTimeOffset.UtcNow,
    };
}
