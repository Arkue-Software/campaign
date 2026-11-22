// RedVital — campaign-service
// Consumidor idempotente de EV-07 institution.changed: guarda la institución
// en institucion_proyeccion, de donde salen el municipio de una campaña nueva y
// el nombre del banco en los listados. En una sola transacción registra el
// evento en evento_procesado. Un evento más viejo que lo guardado no lo pisa:
// manda cambiado_en, no el orden de llegada.

using Microsoft.EntityFrameworkCore;
using RedVital.Campanas.Aplicacion.Eventos;
using RedVital.Campanas.Infraestructura.Persistencia;

namespace RedVital.Campanas.Aplicacion.CasosDeUso;

public class AplicarInstitucionCambiada
{
    private static readonly TimeSpan VigenciaProcesado = TimeSpan.FromDays(14);
    private readonly CampanasDbContext _contexto;

    public AplicarInstitucionCambiada(CampanasDbContext contexto) => _contexto = contexto;

    /// <returns>Falso si el evento ya se había aplicado.</returns>
    public async Task<bool> EjecutarAsync(Guid eventoId, InstitucionCambiadaV1 e, CancellationToken ct = default)
    {
        var ahora = DateTimeOffset.UtcNow;
        await using var transaccion = await _contexto.Database.BeginTransactionAsync(ct);

        var nuevo = await _contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO evento_procesado (grupo_consumidor, evento_id, tipo, procesado_en, expira_en)
            VALUES ({Temas.Grupo}, {eventoId}, {InstitucionCambiadaV1.TipoEvento}, {ahora}, {ahora.Add(VigenciaProcesado)})
            ON CONFLICT (grupo_consumidor, evento_id) DO NOTHING
            """, ct);
        if (nuevo == 0)
        {
            await transaccion.CommitAsync(ct);
            return false;
        }

        await _contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO institucion_proyeccion (institucion_id, codigo, nombre, tipo, territorio_codigo, territorio_ruta,
                                                vigente, cambiado_en, actualizado_en)
            VALUES ({e.InstitucionId}, {e.Codigo}, {e.Nombre}, {e.Tipo}, {e.TerritorioCodigo}, {e.TerritorioRuta},
                    {e.Vigente}, {e.CambiadoEn.ToUniversalTime()}, {ahora})
            ON CONFLICT (institucion_id) DO UPDATE SET
                codigo = EXCLUDED.codigo, nombre = EXCLUDED.nombre, tipo = EXCLUDED.tipo,
                territorio_codigo = EXCLUDED.territorio_codigo, territorio_ruta = EXCLUDED.territorio_ruta,
                vigente = EXCLUDED.vigente, cambiado_en = EXCLUDED.cambiado_en, actualizado_en = EXCLUDED.actualizado_en
            WHERE institucion_proyeccion.cambiado_en <= EXCLUDED.cambiado_en
            """, ct);

        await transaccion.CommitAsync(ct);
        return true;
    }
}
