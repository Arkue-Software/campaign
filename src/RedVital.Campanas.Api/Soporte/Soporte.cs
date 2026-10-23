// RedVital — campaign-service. Correlación (lado servicio de T-305.5) y
// errores RFC 9457 del catálogo del incremento.

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RedVital.Campanas.Aplicacion.CasosDeUso;

namespace RedVital.Campanas.Api.Soporte;

public static class Correlacion
{
    public const string Cabecera = "X-Correlacion-Id";

    public static IApplicationBuilder UsarCorrelacion(this IApplicationBuilder app) =>
        app.Use(async (ctx, siguiente) =>
        {
            var valor = ctx.Request.Headers[Cabecera].ToString();
            if (string.IsNullOrWhiteSpace(valor) || valor.Length > 36) valor = Guid.NewGuid().ToString();
            ctx.Items[Cabecera] = valor;
            ctx.Response.OnStarting(() => { ctx.Response.Headers[Cabecera] = valor; return Task.CompletedTask; });
            await siguiente();
        });

    public static string CorrelacionId(this HttpContext ctx) => ctx.Items[Cabecera] as string ?? Guid.NewGuid().ToString();
}

public static class Problemas
{
    public static IActionResult Desde(Fallo fallo, string correlacionId) => fallo switch
    {
        Fallo.NoPermitido => Crear(403, "operacion-no-permitida", "Operación no permitida", "El rol no autoriza esta operación.", correlacionId),
        Fallo.NoEncontrado => Crear(404, "no-encontrado", "No encontrado", "El recurso no existe dentro de su alcance.", correlacionId),
        Fallo.Conflicto => Crear(409, "conflicto-de-estado", "Conflicto de estado", "El estado actual de la campaña no permite la operación.", correlacionId),
        Fallo.ReglaNegocio => Crear(422, "regla-de-negocio", "Regla de negocio", "La campaña ya terminó y no puede publicarse.", correlacionId),
        _ => Crear(400, "peticion-invalida", "Petición inválida", "La petición no es válida.", correlacionId),
    };

    public static IActionResult SesionInvalida(string correlacionId) =>
        Crear(401, "sesion-invalida", "Sesión inválida", "El token es inválido o expiró.", correlacionId);

    public static IActionResult Crear(int estado, string tipo, string titulo, string detalle, string correlacionId) =>
        new ObjectResult(new { tipo, titulo, estado, detalle, correlacion_id = correlacionId })
        { StatusCode = estado, ContentTypes = { "application/problem+json" } };

    public static Task EscribirAsync(HttpContext ctx, int estado, string tipo, string titulo, string detalle)
    {
        ctx.Response.StatusCode = estado;
        ctx.Response.ContentType = "application/problem+json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new { tipo, titulo, estado, detalle, correlacion_id = ctx.CorrelacionId() }));
    }
}
