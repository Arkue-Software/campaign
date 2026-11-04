// RedVital — campaign-service
// Contratos de eventos (contratos/asyncapi.yaml). Toda comunicación de
// Campañas con los demás servicios pasa por estos temas de Kafka.

using System.Text.Json;

namespace RedVital.Campanas.Aplicacion.Eventos;

public static class Temas
{
    /// <summary>EV-03, productor Campañas.</summary>
    public const string CampaniaPublicada = "redvital.campanias.campaign-published.v1";
    /// <summary>EV-01, productor Donación; Campañas lo consume.</summary>
    public const string DonacionCompletada = "redvital.donacion.donation-completed.v1";
    /// <summary>EV-07, productor Institucional; Campañas lo consume.</summary>
    public const string InstitucionCambiada = "redvital.institucional.institution-changed.v1";
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

/// <summary>EV-07 institution.changed, versión 1: el estado completo de la institución tras el cambio.</summary>
public sealed record InstitucionCambiadaV1(
    Guid InstitucionId, string Codigo, string Nombre, string Tipo, string TerritorioCodigo, string TerritorioRuta,
    bool Vigente, DateTimeOffset CambiadoEn)
{
    public const string TipoEvento = "institution.changed";

    /// <summary>Lo mínimo para guardarla: un evento incompleto va a mensajes muertos.</summary>
    public bool Completo =>
        InstitucionId != Guid.Empty && !string.IsNullOrWhiteSpace(Codigo) && !string.IsNullOrWhiteSpace(Nombre)
        && Tipo is "banco_de_sangre" or "servicio_transfusion"
        && TerritorioCodigo is { Length: 5 } && TerritorioRuta?.StartsWith("/00/") == true && CambiadoEn != default;
}

public static class Json
{
    /// <summary>snake_case y fechas ISO-8601, igual que el resto de contratos.</summary>
    public static readonly JsonSerializerOptions Contrato = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
