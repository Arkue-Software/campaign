// RedVital — campaign-service. Utilidades de arranque en contenedor.

namespace RedVital.Campanas.Api.Soporte;

public static class CadenaConexion
{
    /// <summary>
    /// La contraseña de la base llega como secreto en archivo, nunca en la
    /// cadena versionada. Si la cadena no trae Password, se le agrega.
    /// </summary>
    public static string? ConClave(string? cadena, string? clave) =>
        cadena is null || string.IsNullOrWhiteSpace(clave) || cadena.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            ? cadena
            : $"{cadena.TrimEnd(';')};Password={clave.Trim()}";
}

public static class Salud
{
    public static async Task<int> ComprobarAsync()
    {
        try
        {
            using var cliente = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var puerto = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
            var respuesta = await cliente.GetAsync($"http://127.0.0.1:{puerto}/salud");
            return respuesta.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
