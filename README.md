# campaign-service

Servicio de Campañas de RedVital (.NET 10 / ASP.NET Core). El esquema
`db_campana` y sus migraciones Flyway viven en `Arkue-Software/databases`;
este servicio solo se conecta mediante el rol de ejecución `campana_servicio`.

## Ejecución local

Desde `Arkue-Software/databases`, levanta `db-campana` y `migracion-campana`
siguiendo su README. La base se mantiene en la red interna
`redvital_campana_data` y no publica un puerto al host.

La API ejecutada con `dotnet run` en el host no puede conectarse a la base.
Para ejecutar la API contra PostgreSQL se necesita un contenedor conectado a
`redvital_campana_data`, usando `Host=db-campana;Port=5432`. Este repositorio
aún no incluye Dockerfile ni composición de runtime para ese contenedor.

Una integración funcional también requiere que Campañas e Identidad compartan
una red de aplicación para el descubrimiento JWKS, sin conectar Campañas a la
red de datos de Identidad. No copies llaves privadas a este repositorio.

Mientras no exista la composición de runtime, valida el código con las
pruebas descritas abajo; el build y las pruebas no equivalen a un arranque
funcional conectado a PostgreSQL.

La aplicación no crea ni migra el esquema al iniciar. La URI de descubrimiento
local está en `appsettings.Development.json`; producción usa la configuración
interna de Identidad desde `appsettings.json`.

## Pruebas

Ejecuta `dotnet test RedVital.Campanas.sln` desde la raíz del repositorio.
