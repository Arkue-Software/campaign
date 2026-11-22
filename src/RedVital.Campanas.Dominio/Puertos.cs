// RedVital — campaign-service
// Puertos hacia afuera que no son la base de campañas: viven en Dominio, la
// capa sin dependencias, igual que IRegistradorAuditoriaCamp.

namespace RedVital.Campanas.Dominio;

/// <summary>Institución tal como la publicó el Servicio Institucional (EV-07).</summary>
public sealed record InstitucionResuelta(Guid Id, string Nombre, string Tipo, string TerritorioCodigo, string TerritorioRuta, bool Vigente);

/// <summary>
/// Copia local de las instituciones, que llega por Kafka (EV-07). Campañas no
/// consulta al Servicio Institucional: al crear una campaña toma de aquí el
/// municipio y la ruta del banco, y los listados muestran su nombre.
/// </summary>
public interface IResolutorInstitucion
{
    /// <summary>Nulo si todavía no llegó el evento de esa institución.</summary>
    Task<InstitucionResuelta?> ResolverAsync(Guid institucionId, CancellationToken ct = default);

    /// <summary>Nombre de cada institución conocida; las que no han llegado no aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, string>> NombresAsync(IReadOnlyCollection<Guid> instituciones, CancellationToken ct = default);
}
