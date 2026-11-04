// RedVital — campaign-service. Conecta T-312.2, T-312.3, T-312.4 y T-312.5.
// Validación de token propia (EC-03), sin preguntarle nada a Identidad:
//  - Development: con la llave PÚBLICA de prueba de T-303.1 (T-303.2).
//  - QA/Production: con la llave pública de firma de Identidad, montada como
//    secreto (identidad_llave_publica). Ese perfil no declara la llave de
//    prueba, por eso la rechaza (T-303.3).
// Con los demás servicios solo habla por Kafka.

using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RedVital.Campanas.Api.Mantenimiento;
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
builder.Services.AddScoped<CrearCampania>();           // POST /v1/campanias
builder.Services.AddScoped<EditarCampania>();          // PUT /v1/campanias/{id}
builder.Services.AddScoped<IResolutorInstitucion, InstitucionesProyectadas>(); // copia local de EV-07
builder.Services.AddScoped<AplicarInstitucionCambiada>(); // consumidor de EV-07
builder.Services.AddHostedService<DepuradorRegistrosTecnicos>(); // bandeja publicada y eventos procesados vencidos

// Kafka: único canal entre Campañas y los demás servicios (ADR-018).
var kafka = conf.GetSection("Kafka").Get<OpcionesKafka>() ?? new OpcionesKafka();
kafka.Clave ??= conf["kafka_campanias"];
if (kafka.Habilitado)
{
    builder.Services.AddSingleton(kafka);
    builder.Services.AddSingleton<ProductorKafka>();
    builder.Services.AddHostedService<PublicadorBandeja>();
    builder.Services.AddHostedService<ConsumidorDonacionCompletada>();
    builder.Services.AddHostedService<ConsumidorInstitucionCambiada>();
}

// La llave pública de firma de Identidad se lee al arrancar: si falta o no es
// válida, el servicio no arranca, en vez de responder 500 a cada petición.
var llavePublicaDev = conf["Jwt:LlavePublicaDesarrollo"];
var llaveFirma = LlavePublica.Cargar(builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(llavePublicaDev)
    ? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, llavePublicaDev))
    : conf["Jwt:LlavePublica"] ?? "/run/secrets/identidad_llave_publica");

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

    o.TokenValidationParameters.IssuerSigningKey = llaveFirma;

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

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; // cupo_disponible, publicada_en…
        // Una propiedad desconocida en el cuerpo es un 400, como en Donación e Institucional.
        o.JsonSerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    })
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = c =>
        Problemas.Crear(400, "peticion-invalida", "Petición inválida", "El cuerpo no cumple el contrato.", c.HttpContext.CorrelacionId()));

var app = builder.Build();
app.UsarCorrelacion();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/salud");
await app.RunAsync();
return 0;

public partial class Program { }
