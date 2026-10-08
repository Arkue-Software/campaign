// RedVital — campaign-service
// T-312.3 — Detalle de campaña para usuario y para el token de servicio de
// Donación (GET /v1/campanias/{id}). Requiere token.
//  - Donante: solo "publicada" o "cerrada".
//  - Institucionales: solo dentro de su jurisdicción.
//  - El Servicio de Donación no consulta por HTTP: toda comunicación entre
//    Donación y Campañas pasa por Kafka (campaign.published).
// Fuera de alcance responde 404 "no-encontrado": no confirma que exista
// (RNF-02), pero el intento SÍ queda auditado con la jurisdicción solicitada.

using Microsoft.EntityFrameworkCore;
using RedVital.Campanas.Dominio;
using RedVital.Campanas.Infraestructura.Persistencia;

namespace RedVital.Campanas.Aplicacion.CasosDeUso;

public class ConsultarCampania
{
    private readonly CampanasDbContext _contexto;
    private readonly IRegistradorAuditoriaCamp _auditoria;

    public ConsultarCampania(CampanasDbContext contexto, IRegistradorAuditoriaCamp auditoria)
    {
        _contexto = contexto;
        _auditoria = auditoria;
    }

    public async Task<Resultado<CampaniaRespuesta>> EjecutarAsync(Solicitante s, Guid id, string correlacionId, CancellationToken ct = default)
    {
        if (!(s.EsDonante || s.EsInstitucional))
        {
            await _auditoria.RegistrarAsync(Auditar.Hecho(s, "consultar_campania", id, "denegado", correlacionId), ct);
            return Resultado<CampaniaRespuesta>.Error(Fallo.NoPermitido);
        }

        var c = await _contexto.Campanias.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Resultado<CampaniaRespuesta>.Error(Fallo.NoEncontrado);

        var visible =
            (s.EsDonante && c.Estado is "publicada" or "cerrada") ||
            (s.EsInstitucional && s.Alcanza(c) && (c.Estado != "borrador" || s.EsAdminBanco));

        if (!visible)
        {
            if (s.EsInstitucional && !s.Alcanza(c))
                await _auditoria.RegistrarAsync(Auditar.Hecho(s, "consultar_campania", id, "denegado", correlacionId,
                    jurisdiccionSolicitada: $"territorio:{c.TerritorioRuta}"), ct);
            return Resultado<CampaniaRespuesta>.Error(Fallo.NoEncontrado);
        }

        return Resultado<CampaniaRespuesta>.Ok(CampaniaRespuesta.Desde(c, s.EsInstitucional));
    }
}
