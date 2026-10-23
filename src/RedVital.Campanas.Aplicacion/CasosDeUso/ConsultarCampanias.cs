// RedVital — campaign-service
// T-312.2 — Consulta de campañas filtrada por rol y jurisdicción
// (GET /v1/campanias). Reglas tomadas de campanas-openapi.yaml:
//  - Sin token o donante: solo "publicada", filtradas por el territorio elegido.
//  - U3, U4, U5, U6: solo las de su jurisdicción (filtro en la consulta SQL,
//    no en memoria: nunca se traen filas ajenas a la aplicación).
//  - El Servicio de Donación ya no consulta: recibe las campañas por Kafka.
//  - U7 (auditor) y cualquier otro: 403, y el intento queda auditado.

using Microsoft.EntityFrameworkCore;
using RedVital.Campanas.Dominio;
using RedVital.Campanas.Infraestructura.Persistencia;

namespace RedVital.Campanas.Aplicacion.CasosDeUso;

public record ConsultaCampanias(string? Estado, DateTimeOffset? PublicadaDesde, string? Territorio);

public class ConsultarCampanias
{
    private readonly CampanasDbContext _contexto;
    private readonly IRegistradorAuditoriaCamp _auditoria;

    public ConsultarCampanias(CampanasDbContext contexto, IRegistradorAuditoriaCamp auditoria)
    {
        _contexto = contexto;
        _auditoria = auditoria;
    }

    public async Task<Resultado<List<CampaniaRespuesta>>> EjecutarAsync(
        Solicitante s, ConsultaCampanias filtro, string correlacionId, CancellationToken ct = default)
    {
        IQueryable<Campania> q = _contexto.Campanias.AsNoTracking();

        if (s.EsAnonimo || s.EsDonante)
        {
            q = q.Where(c => c.Estado == "publicada");
            // Hueco del contrato: la descripción pide filtrar "por el municipio o
            // departamento seleccionado", pero no define el parámetro. Se usa
            // "territorio" (ruta DIVIPOLA) hasta que Sara lo agregue al contrato.
            if (!string.IsNullOrWhiteSpace(filtro.Territorio))
            {
                var ruta = filtro.Territorio;
                q = q.Where(c => c.TerritorioRuta == ruta || c.TerritorioRuta.StartsWith(ruta + "/"));
            }
        }
        else if (s.EsInstitucional)
        {
            if (s.InstitucionJurisdiccion is { } inst)
                q = q.Where(c => c.InstitucionId == inst);
            else if (s.RutaJurisdiccion is { } ruta)
                q = q.Where(c => c.TerritorioRuta == ruta || c.TerritorioRuta.StartsWith(ruta + "/"));
            else
                return await DenegarAsync(s, correlacionId, ct); // institucional sin jurisdicción: nunca "todo"

            // Supuesto a validar con Sara: los borradores solo los ve U4 (quien los crea).
            if (!s.EsAdminBanco) q = q.Where(c => c.Estado != "borrador");
            if (filtro.Estado is not null) q = q.Where(c => c.Estado == filtro.Estado);
        }
        else
        {
            return await DenegarAsync(s, correlacionId, ct);
        }

        var lista = await q.OrderBy(c => c.IniciaEn).ToListAsync(ct);
        return Resultado<List<CampaniaRespuesta>>.Ok(lista.Select(c => CampaniaRespuesta.Desde(c, s.EsInstitucional)).ToList());
    }

    private async Task<Resultado<List<CampaniaRespuesta>>> DenegarAsync(Solicitante s, string correlacionId, CancellationToken ct)
    {
        await _auditoria.RegistrarAsync(Auditar.Hecho(s, "consultar_campanias", null, "denegado", correlacionId), ct);
        return Resultado<List<CampaniaRespuesta>>.Error(Fallo.NoPermitido);
    }
}
