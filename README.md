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

Los tokens se validan con la llave pública de firma de Identidad, montada como
secreto (`Jwt__LlavePublica`, por defecto `/run/secrets/identidad_llave_publica`):
Campañas no consulta a Identidad. No copies llaves privadas a este repositorio.

Mientras no exista la composición de runtime, valida el código con las
pruebas descritas abajo; el build y las pruebas no equivalen a un arranque
funcional conectado a PostgreSQL.

La aplicación no crea ni migra el esquema al iniciar.

## Pruebas

Ejecuta `dotnet test RedVital.Campanas.sln` desde la raíz del repositorio.

## Comunicación con los demás servicios

Campañas no llama a ningún servicio ni recibe llamadas de ellos: todo pasa por Kafka. Contratos en [`contratos/asyncapi.yaml`](contratos/asyncapi.yaml).

- Publica EV-03 `campaign-published` y EV-10 `audit-recorded` (cada registro de `registro_auditoria_camp`, para la bitácora de Institucional), por la bandeja de salida y en la misma transacción que el cambio.
- Consume EV-01 `donation-completed` (cuenta las donaciones de cada campaña) y EV-07 `institution-changed`, con el que mantiene su copia de las instituciones (`institucion_proyeccion`).
- `POST /v1/campanias` (U4) crea la campaña en borrador con la institución del token; su municipio y su ruta salen de esa copia. Si la institución todavía no ha llegado, responde 503 y no crea nada; si está dada de baja, 409. `PUT /v1/campanias/{id}` edita un borrador.
- Los listados y el detalle traen `institucion_nombre`, también de esa copia, para que la web muestre el banco sin preguntarle a nadie.
