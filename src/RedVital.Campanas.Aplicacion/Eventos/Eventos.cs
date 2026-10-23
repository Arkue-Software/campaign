// RedVital — campaign-service
// Contratos de eventos con Donación (contratos/asyncapi.yaml). Toda
// comunicación entre Campañas y Donación pasa por estos temas de Kafka.

using System.Text.Json;

namespace RedVital.Campanas.Aplicacion.Eventos;

public static class Temas
{
    /// <summary>EV-03, productor Campañas.</summary>
    public const string CampaniaPublicada = "redvital.campanias.campaign-published.v1";
    /// <summary>EV-01, productor Donación; Campañas lo consume.</summary>
    public const string DonacionCompletada = "redvital.donacion.donation-completed.v1";
    /// <summary>Grupo consumidor de Campañas (DD, sección 14).</summary>
    public const string Grupo = "campanias";
    public const string SufijoMensajesMuertos = ".campanias.dlt";
}

/// <summary>EV-03 campaign.published, versión 1. Sin datos de donantes ni de reservas.</summary>
public sealed record CampaniaPublicadaV1(
    Guid CampaniaId, Guid InstitucionId, string Nombre, string Sede, string TerritorioCodigo,
    string TerritorioRuta, DateTimeOffset IniciaEn, DateTimeOffset TerminaEn, DateTimeOffset PublicadaEn)
{
    public const string Tipo = "campaign.published";
    public const int Version = 1;
}

/// <summary>EV-01 donation.completed, tal como lo publica Donación. Campañas solo usa dos campos.</summary>
public sealed record DonacionCompletadaV1(Guid DonacionId, Guid? CampaniaId)
{
    public const string Tipo = "donation.completed";
}

public static class Json
{
    /// <summary>snake_case y fechas ISO-8601, igual que el resto de contratos.</summary>
    public static readonly JsonSerializerOptions Contrato = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
