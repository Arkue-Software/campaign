// RedVital — campaign-service. Conecta T-312.2, T-312.3, T-312.4 y T-312.5.
// Validación de token propia (EC-03):
//  - Development: con la llave PÚBLICA de prueba de T-303.1 (T-303.2), así se
//    prueba Campañas sin tener Identidad corriendo.
//  - QA/Production: con el JWKS real de identity-service (T-304.4). Ese
//    perfil no declara la llave de prueba, por eso la rechaza (T-303.3).

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RedVital.Campanas.Api.Mensajeria;
using RedVital.Campanas.Api.Soporte;
using RedVital.Campanas.Aplicacion.CasosDeUso;
using RedVital.Campanas.Dominio;
using RedVital.Campanas.Infraestructura.Persistencia;

// Comprobación de salud del contenedor: la imagen no tiene curl, así que el
// propio ejecutable consulta /salud y termina con 0 o 1.
if (args.Contains("--salud")) return await Salud.ComprobarAsync();

var builder = WebApplication.CreateBuilder(args);
var conf = builder.Configuration;

// Secretos montados como archivos (/run/secrets/<nombre>): campana_servicio y kafka_campanias.
conf.AddKeyPerFile(conf["Secretos:Directorio"] ?? "/run/secrets", optional: true);

builder.Services.AddDbContext<CampanasDbContext>(o =>
    o.UseNpgsql(CadenaConexion.ConClave(conf.GetConnectionString("Campanas"), conf["campana_servicio"])
        ?? throw new InvalidOperationException("Falta ConnectionStrings:Campanas.")));
builder.Services.AddHealthChecks();

builder.Services.AddScoped<IRegistradorAuditoriaCamp, RegistradorAuditoriaCamp>(); // T-312.5
builder.Services.AddScoped<ConsultarCampanias>();     // T-312.2
builder.Services.AddScoped<ConsultarCampania>();      // T-312.3
builder.Services.AddScoped<CambiarEstadoCampania>();  // T-312.4
builder.Services.AddScoped<AplicarDonacionCompletada>(); // RF-25, consumidor de EV-01

// Kafka: único canal entre Campañas y Donación (ADR-018).
var kafka = conf.GetSection("Kafka").Get<OpcionesKafka>() ?? new OpcionesKafka();
kafka.Clave ??= conf["kafka_campanias"];
if (kafka.Habilitado)
{
    builder.Services.AddSingleton(kafka);
    builder.Services.AddSingleton<ProductorKafka>();
    builder.Services.AddHostedService<PublicadorBandeja>();
    builder.Services.AddHostedService<ConsumidorDonacionCompletada>();
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = conf["Jwt:Emisor"],
        ValidAudience = conf["Jwt:Audiencia"],
        ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
        NameClaimType = "sub",
        RoleClaimType = "role",
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    var llavePublicaDev = conf["Jwt:LlavePublicaDesarrollo"];
    if (builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(llavePublicaDev))
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, llavePublicaDev))));
        o.TokenValidationParameters.IssuerSigningKey = new RsaSecurityKey(rsa) { KeyId = "dev-key-1" };
    }
    else
    {
        // Descubrimiento → jwks_uri de identity-service, por la red interna.
        o.MetadataAddress = conf["Jwt:Descubrimiento"]!;
        o.RequireHttpsMetadata = false; // red_aplicacion interna; TLS termina en Caddy
    }

    o.Events = new JwtBearerEvents
    {
        OnChallenge = async c =>
        {
            c.HandleResponse();
            await Problemas.EscribirAsync(c.HttpContext, 401, "sesion-invalida", "Sesión inválida", "El token es inválido o expiró.");
        },
    };
});
builder.Services.AddAuthorization();

builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower); // cupo_disponible, publicada_en…

var app = builder.Build();
app.UsarCorrelacion();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/salud");
await app.RunAsync();
return 0;

public partial class Program { }
