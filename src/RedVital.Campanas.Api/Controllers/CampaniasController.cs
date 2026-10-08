// RedVital — campaign-service
// GET  /v1/campanias                  → T-312.2 (pública, token opcional)
// GET  /v1/campanias/{id}             → T-312.3 (requiere token)
// POST /v1/campanias/{id}/publicacion → T-312.4
// POST /v1/campanias/{id}/cierre      → T-312.4

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedVital.Campanas.Api.Soporte;
using RedVital.Campanas.Aplicacion.CasosDeUso;
using RedVital.Campanas.Dominio;

namespace RedVital.Campanas.Api.Controllers;

[ApiController]
[Route("v1/campanias")]
public class CampaniasController : ControllerBase
{
    private readonly ConsultarCampanias _consultar;
    private readonly ConsultarCampania _detalle;
    private readonly CambiarEstadoCampania _cambiarEstado;

    public CampaniasController(ConsultarCampanias consultar, ConsultarCampania detalle, CambiarEstadoCampania cambiarEstado)
    {
        _consultar = consultar;
        _detalle = detalle;
        _cambiarEstado = cambiarEstado;
    }

    /// <summary>T-312.2</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> ConsultarAsync(
        [FromQuery] string? estado, [FromQuery(Name = "publicada_desde")] DateTimeOffset? publicadaDesde,
        [FromQuery] string? territorio, CancellationToken ct)
    {
        var correlacionId = HttpContext.CorrelacionId();

        // Operación pública con token opcional: si llega un token, tiene que ser
        // válido. Uno inválido no se degrada a "anónimo" en silencio.
        if (Request.Headers.ContainsKey("Authorization") && User.Identity?.IsAuthenticated != true)
            return Problemas.SesionInvalida(correlacionId);

        var r = await _consultar.EjecutarAsync(Solicitante_(User), new ConsultaCampanias(estado, publicadaDesde, territorio), correlacionId, ct);
        return r.Exitoso ? Ok(r.Valor) : Problemas.Desde(r.Fallo, correlacionId);
    }

    /// <summary>T-312.3</summary>
    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> DetalleAsync(Guid id, CancellationToken ct)
    {
        var correlacionId = HttpContext.CorrelacionId();
        var r = await _detalle.EjecutarAsync(Solicitante_(User), id, correlacionId, ct);
        return r.Exitoso ? Ok(r.Valor) : Problemas.Desde(r.Fallo, correlacionId);
    }

    /// <summary>T-312.4</summary>
    [HttpPost("{id:guid}/publicacion")]
    [Authorize]
    public Task<IActionResult> PublicarAsync(Guid id, CancellationToken ct) => CambiarAsync(id, Accion.Publicar, ct);

    /// <summary>T-312.4</summary>
    [HttpPost("{id:guid}/cierre")]
    [Authorize]
    public Task<IActionResult> CerrarAsync(Guid id, CancellationToken ct) => CambiarAsync(id, Accion.Cerrar, ct);

    private async Task<IActionResult> CambiarAsync(Guid id, Accion accion, CancellationToken ct)
    {
        var correlacionId = HttpContext.CorrelacionId();
        var r = await _cambiarEstado.EjecutarAsync(Solicitante_(User), id, accion, correlacionId, ct);
        return r.Exitoso ? Ok(r.Valor) : Problemas.Desde(r.Fallo, correlacionId);
    }

    private static Solicitante Solicitante_(ClaimsPrincipal u) =>
        u.Identity?.IsAuthenticated == true
            ? new Solicitante(u.FindFirst("sub")?.Value, u.FindFirst("role")?.Value, u.FindFirst("jurisdiction")?.Value)
            : Solicitante.Anonimo;
}
