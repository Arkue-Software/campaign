// T-303.3 (lado Campañas): el perfil de QA no carga la llave de prueba.

using Xunit;

namespace RedVital.Campanas.Pruebas;

public class ConfiguracionQaPruebas
{
    [Fact]
    public void AppSettings_de_QA_no_referencia_la_llave_de_prueba()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        var qa = File.ReadAllText(Path.Combine(dir!.FullName, "src", "RedVital.Campanas.Api", "appsettings.QA.json"));

        Assert.DoesNotContain("LlavePublicaDesarrollo", qa);
        Assert.DoesNotContain("dev-signing-key", qa);
    }
}
