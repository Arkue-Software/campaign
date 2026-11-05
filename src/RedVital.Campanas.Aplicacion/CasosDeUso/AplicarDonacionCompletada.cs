// RedVital — campaign-service
// Consumidor idempotente de EV-01 donation.completed (RF-25, D-09).
// En una sola transacción: registra el evento en evento_procesado y suma la
// donación a su campaña. Si el evento ya estaba registrado es un duplicado y
// no tiene efecto. El incremento es atómico en la base, de modo que dos
// instancias del servicio nunca pierden una suma.

using Microsoft.EntityFrameworkCore;
using RedVital.Campanas.Aplicacion.Eventos;
using RedVital.Campanas.Infraestructura.Persistencia;

namespace RedVital.Campanas.Aplicacion.CasosDeUso;

public class AplicarDonacionCompletada
{
    private static readonly TimeSpan VigenciaProcesado = TimeSpan.FromDays(14);
    private readonly CampanasDbContext _contexto;

    public AplicarDonacionCompletada(CampanasDbContext contexto) => _contexto = contexto;

    /// <returns>Falso si el evento ya se había aplicado.</returns>
    public async Task<bool> EjecutarAsync(Guid eventoId, DonacionCompletadaV1 evento, CancellationToken ct = default)
    {
        var ahora = DateTimeOffset.UtcNow;
        await using var transaccion = await _contexto.Database.BeginTransactionAsync(ct);

        var nuevo = await _contexto.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO evento_procesado (grupo_consumidor, evento_id, tipo, procesado_en, expira_en)
            VALUES ({Temas.Grupo}, {eventoId}, {DonacionCompletadaV1.Tipo}, {ahora}, {ahora.Add(VigenciaProcesado)})
            ON CONFLICT (grupo_consumidor, evento_id) DO NOTHING
            """, ct);
        if (nuevo == 0)
        {
            await transaccion.CommitAsync(ct);
            return false;
        }

        // Una donación sin campaña, o de una campaña que Campañas no conoce, no se cuenta.
        if (evento.CampaniaId is { } campania)
        {
            await _contexto.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE campania SET donaciones_registradas = donaciones_registradas + 1, actualizada_en = {ahora}
                WHERE id = {campania}
                """, ct);
        }

        await transaccion.CommitAsync(ct);
        return true;
    }
}
