# Inventario y Ventas: microservicios .NET con cliente Vue

Tres aplicaciones ASP.NET Core con comunicación HTTP:

| Proyecto | Responsabilidad | Datos propios |
|---|---|---|
| `src/Integration.Api` | Gateway, acceso de usuarios, Swagger y página Vue | `users.db` |
| `src/Inventory.Api` | Artículos, existencias y reservas | `inventory.db` |
| `src/Sales.Api` | Clientes, vendedores, ventas y recuperación de operaciones | `sales.db` |
| `src/Integration.Contracts` | Contratos JSON y convenciones HTTP compartidas | Sin almacenamiento |

Inventario no consulta la base de Ventas y Ventas no consulta la de Inventario. `productId` es una referencia entre servicios, sin clave foránea entre sus bases. Los dos clientes de consola existentes siguen consumiendo las rutas del gateway.

## Iniciar todo con un comando

Requisitos de desarrollo: SDK .NET 8 o superior con runtime ASP.NET Core 8, Node.js 22.12 o superior, npm y acceso a NuGet/npm para restaurar dependencias.

```powershell
dotnet run --project src/Integration.Api --launch-profile Integration.Api
```

Abra [https://localhost:62702](https://localhost:62702). El gateway compila Vue y arranca Inventario y Ventas como **dos procesos separados**, sin necesitar Vite. Visual Studio puede iniciar `Integration.Api` con F5 y abrir esa misma página. Compruebe que el certificado de desarrollo HTTPS sea de confianza.

Puertos predeterminados: gateway HTTPS `62702` y HTTP `62703`, Inventario HTTP de loopback `62711`, Ventas HTTP de loopback `62712`. El arranque espera a que los dos servicios respondan antes de aceptar solicitudes en el gateway. Si un puerto está ocupado, avisa y detiene los procesos que haya creado. Al cerrar el gateway, incluso de forma abrupta, los servicios que inició también se detienen. Los servicios iniciados por separado se administran de forma independiente. Las bases conservan los datos.

Las dependencias de npm se restauran cuando faltan o cambian los archivos de paquetes. Vue se compila en cada build y se copia a `src/Integration.Api/wwwroot`, una carpeta generada e ignorada por Git. `--no-build` usa la compilación anterior. Para recarga automática de la interfaz puede seguir usando `npm run dev` dentro de `web-client` en el puerto `5173`.

## Probar desde la página

En el primer inicio local con el perfil de desarrollo, cree el usuario inicial desde la pantalla de acceso; la contraseña debe tener al menos 10 caracteres. Después inicie sesión y use el menú principal para gestionar Clientes, Artículos y Vendedores. Puede crear un artículo con precio `150000` centavos y stock `10` y registrar una venta de `3` unidades. Debe aparecer como **Confirmada**, con total RD$ 4,500.00 y stock disponible `7`.

Clientes incluye nombre comercial o razón social, RNC o cédula, cuenta contable y estado. Artículos incluye descripción, precio unitario, estado y existencias. Vendedores incluye nombre, comisión porcentual con hasta dos decimales y estado. Los artículos existentes se conservan al actualizar el esquema y quedan activos. Un artículo inactivo no se puede vender.

- Reenviar la misma referencia con el mismo producto y cantidad devuelve la venta original, sin un descuento adicional.
- Reutilizar la referencia con otros datos devuelve 409.
- Una falta de existencias devuelve 409 y deja una venta **Rechazada** con su motivo. Para otra operación debe usar otra referencia.
- Si Inventario no responde, la venta queda **Pendiente** o **Confirmando**, con respuesta 202. Ventas la reintenta automáticamente cada tres segundos.
- Una venta Pendiente se puede cancelar desde el historial. **Cancelando** indica que aún falta confirmar la liberación en Inventario. Una venta Confirmando o Confirmada no se cancela en esta práctica.
- Cambiar el stock de un producto con una reserva activa devuelve 409. Eliminar un producto con historial de reservas o ventas también devuelve 409.

Swagger está en [https://localhost:62702/swagger](https://localhost:62702/swagger). El registro de solicitudes de la web muestra estados HTTP y cuerpos JSON. El historial incluye operaciones rechazadas y pendientes, además de las confirmadas.

## Configuración

Variables del gateway:

| Variable | Predeterminado | Función |
|---|---|---|
| `API_KEY` | Sin clave pública predeterminada | Clave opcional para integraciones y aplicaciones de consola; configurarla explícitamente en el gateway y en cada cliente que la utilice |
| `SERVICE_API_KEY` | Aleatoria en el arranque conjunto; `local-service-key` en servicios independientes sin configuración | Clave para comunicación entre servicios. En despliegues independientes, configúrela expresamente y use el mismo valor en los tres procesos |
| `AUTH_DATABASE_PATH` | `data/users.db` relativo al directorio del gateway | Base de usuarios, independiente de las bases de negocio |
| `ADMIN_USERNAME`, `ADMIN_PASSWORD` | Sin valores predeterminados | Crean el primer usuario al arrancar si todavía no existe; contraseña de 10 a 200 caracteres |
| `ALLOW_LOCAL_SETUP` | `false` fuera de Desarrollo | Permite habilitar explícitamente la configuración inicial desde loopback en otros entornos |
| `START_LOCAL_SERVICES` | `true` | Arrancar y detener los servicios como procesos hijos |
| `Inventory__Url` | `http://127.0.0.1:62711` | Dirección de Inventario |
| `Sales__Url` | `http://127.0.0.1:62712` | Dirección de Ventas |
| `Inventory__DatabasePath` | `data/inventory.db` dentro del directorio del gateway | Base usada al arrancar Inventario localmente |
| `Sales__DatabasePath` | `data/sales.db` dentro del directorio del gateway | Base usada al arrancar Ventas localmente |

Cada microservicio acepta `DATABASE_PATH` para **su propia** base y `SERVICE_API_KEY`. Ventas también acepta `Inventory__Url`. Los endpoints internos exigen la clave de servicio. El gateway valida la sesión de usuario o, si se configuró, la clave de integración, y utiliza la clave interna al reenviar. `/health`, Swagger y el estado de configuración del acceso son públicos.

Las contraseñas se guardan como hash con el componente de ASP.NET Core; la sesión utiliza una cookie HttpOnly y SameSite Strict. No hay contraseña ni clave pública fija incluidas en la web. El primer usuario se crea una sola vez: en Desarrollo, desde el equipo local; en un servidor, configure `ADMIN_USERNAME` y `ADMIN_PASSWORD` antes del primer arranque. `ALLOW_LOCAL_SETUP=true` permite configurar desde loopback fuera de Desarrollo. El usuario inicial puede crear cuentas adicionales desde **Usuarios**; esas cuentas acceden a las gestiones, pero no pueden crear más usuarios. Esta entrega no incluye modificación, desactivación ni recuperación de cuentas.

Para usar los clientes de consola, configure `API_KEY` con el mismo valor en el proceso del gateway y en la terminal del cliente. La clave anterior `local-demo-key` ya no se habilita automáticamente.

Para despliegues remotos configure HTTPS, una clave interna propia y rutas persistentes para las tres bases. La comunicación HTTP sin TLS predeterminada está limitada a loopback. No coloque contraseñas ni `SERVICE_API_KEY` en Vue o variables `VITE_*`. Conserve de forma persistente las claves de protección de datos de ASP.NET Core entre reinicios o réplicas si desea mantener sesiones existentes.

## Ejecutar y desplegar los servicios por separado

El arranque conjunto es una comodidad local, no una dependencia de despliegue. En tres terminales puede ejecutar lo siguiente; configure en todas la misma `SERVICE_API_KEY` (las variables de entorno de una terminal no se comparten con las demás):

```powershell
# Terminal 1: Inventario
$env:SERVICE_API_KEY = "clave-interna-local"
$env:DATABASE_PATH = "$PWD\data\inventory.db"
dotnet run --project src/Inventory.Api -- --urls http://127.0.0.1:62711
```

```powershell
# Terminal 2: Ventas
$env:SERVICE_API_KEY = "clave-interna-local"
$env:DATABASE_PATH = "$PWD\data\sales.db"
$env:Inventory__Url = "http://127.0.0.1:62711"
dotnet run --project src/Sales.Api -- --urls http://127.0.0.1:62712
```

```powershell
# Terminal 3: gateway y web, sin crear procesos hijos
$env:SERVICE_API_KEY = "clave-interna-local"
$env:START_LOCAL_SERVICES = "false"
$env:Inventory__Url = "http://127.0.0.1:62711"
$env:Sales__Url = "http://127.0.0.1:62712"
dotnet run --project src/Integration.Api --launch-profile Integration.Api
```

Para direcciones remotas, mantenga `START_LOCAL_SERVICES=false` y configure las URL reales. Cada servicio puede publicarse con `dotnet publish` sobre su propio `.csproj`. En ese modo, el gestor de procesos o plataforma de despliegue administra los reinicios de cada servicio. El gateway no reinicia automáticamente un proceso hijo que falle después del arranque.

## Publicar la web y los tres procesos juntos

```powershell
dotnet publish src/Integration.Api/Integration.Api.csproj -c Release -o artifacts/publish
```

El paquete contiene el gateway, `wwwroot` y las publicaciones independientes en `services/inventory` y `services/sales`. Copie la carpeta completa. En el servidor solo se necesita el runtime ASP.NET Core 8 para esta publicación dependiente del framework; Node.js y el SDK no son necesarios.

```powershell
Set-Location artifacts/publish
dotnet Integration.Api.dll --urls https://localhost:62702
```

Con `START_LOCAL_SERVICES=true` (valor predeterminado) se arrancan los dos DLL de `services/`. Detenga antes otras instancias que utilicen esos puertos. En otro equipo configure su dominio y certificado; las bases deberían guardarse en rutas persistentes mediante las variables de configuración.

## Datos de la versión anterior

La nueva arquitectura no utiliza `integration.db`. El archivo se conserva. Para trasladar una instalación anterior, detenga los servicios y ejecute una sola vez, antes de que se creen las bases nuevas:

```powershell
dotnet run --project tools/Migrate.Legacy -- src/Integration.Api/data/integration.db src/Integration.Api/data/inventory.db src/Integration.Api/data/sales.db
```

La herramienta conserva IDs, referencias, stock y totales. Registra las ventas históricas como Confirmadas y sus reservas como ya confirmadas, sin volver a descontar stock. Rechaza destinos existentes y no modifica el origen. Las rutas se pueden cambiar si la instalación utiliza otro directorio.

## Verificación

```powershell
dotnet test Integracion.sln
./scripts/Test-PublishedApp.ps1 -Development
dotnet publish src/Integration.Api/Integration.Api.csproj -c Release -o artifacts/publish
./scripts/Test-PublishedApp.ps1
```

Las pruebas .NET utilizan tres servidores HTTP, bases temporales distintas e inyección de pérdidas de respuestas. Cubren el flujo de venta, concurrencia, idempotencia, reinicios, cancelación, disponibilidad parcial, validación, autenticación, CORS y migración. El script comprueba el arranque con tres procesos reales, la web compilada y una venta completa; no modifica las bases de la aplicación.

Para las pruebas del cliente HTTP de Vue: `npm test` dentro de `web-client`.

[Diseño y protocolo entre servicios](docs/DISENO.md) · [Preparación del caso de negocio](docs/PREPARACION.md) · [Cliente Vue](web-client/README.md)
