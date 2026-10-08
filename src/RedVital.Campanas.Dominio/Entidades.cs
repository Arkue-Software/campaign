// RedVital — campaign-service
// Mapeadas contra databases/campanas/schema.sql (no generan el esquema).

namespace RedVital.Campanas.Dominio;

public class Campania
{
    public Guid Id { get; set; }
    public Guid InstitucionId { get; set; }
    public required string TerritorioCodigo { get; set; }
    public required string TerritorioRuta { get; set; }
    public required string Nombre { get; set; }
    public string? Descripcion { get; set; }
    public required string Sede { get; set; }
    public DateTimeOffset IniciaEn { get; set; }
    public DateTimeOffset TerminaEn { get; set; }
    public int? CupoTotal { get; set; }
    /// <summary>Uso interno. Nunca se expone: el contrato publica cupo_disponible.</summary>
    public int CupoReservado { get; set; }
    public required string Estado { get; set; }
    public DateTimeOffset? PublicadaEn { get; set; }
    public Guid CreadaPor { get; set; }
    public DateTimeOffset CreadaEn { get; set; }
    public DateTimeOffset ActualizadaEn { get; set; }
    /// <summary>RF-25: donaciones registradas en la campaña, contadas al consumir donation.completed.</summary>
    public int DonacionesRegistradas { get; set; }

    public int? CupoDisponible => CupoTotal is null ? null : Math.Max(0, CupoTotal.Value - CupoReservado);
}

public class ReservaCupo
{
    public Guid Id { get; set; }
    public Guid CampaniaId { get; set; }
    public Guid UsuarioId { get; set; }
    public required string Estado { get; set; }
    public DateTimeOffset CreadaEn { get; set; }
    public DateTimeOffset ExpiraEn { get; set; }
    public DateTimeOffset? ConfirmadaEn { get; set; }
    public DateTimeOffset? CerradaEn { get; set; }
}

/// <summary>
/// Bandeja de salida (ADR-018). Se escribe en la misma transacción que el
/// hecho; un publicador del servicio la drena hacia Kafka en orden.
/// </summary>
public class EventoSalida
{
    public Guid Id { get; set; }
    public long Secuencia { get; set; }
    public required string Tipo { get; set; }
    public int VersionEsquema { get; set; }
    public required string Tema { get; set; }
    public required string Clave { get; set; }
    public required string Carga { get; set; }
    public string? ClaveNatural { get; set; }
    public required string CorrelacionId { get; set; }
    public string Estado { get; set; } = "pendiente";
    public int IntentosPublicacion { get; set; }
    public DateTimeOffset CreadoEn { get; set; }
    public DateTimeOffset? PublicadoEn { get; set; }
    public DateTimeOffset? ExpiraEn { get; set; }
}

/// <summary>T-312.5 — serie de auditoría de Campañas (13 campos comunes).</summary>
public class RegistroAuditoriaCamp
{
    public Guid Id { get; set; }
    public required string ActorTipo { get; set; }
    public string? ActorId { get; set; }
    public string? Rol { get; set; }
    public string? JurisdiccionSolicitada { get; set; }
    public required string Operacion { get; set; }
    public required string RecursoTipo { get; set; }
    public Guid? RecursoId { get; set; }
    public required string Resultado { get; set; }
    public required string CorrelacionId { get; set; }
    public string? Origen { get; set; }
    public DateTimeOffset OcurridoEn { get; set; }
}

public interface IRegistradorAuditoriaCamp
{
    Task RegistrarAsync(RegistroAuditoriaCamp registro, CancellationToken ct = default);
}
