# Cliente Vue: facturación e inventario

La aplicación incluye acceso de usuarios, menú principal y gestiones de Clientes, Artículos, Vendedores y Ventas. Se comunica con el gateway ASP.NET Core y los microservicios de Inventario y Ventas.

## Iniciar todo junto

Desde la raíz de la solución, con el SDK .NET y Node.js 22.12 o superior instalados:

```powershell
dotnet run --project src/Integration.Api --launch-profile Integration.Api
```

Abra https://localhost:62702. Visual Studio también puede iniciar `Integration.Api` con F5. La compilación .NET genera Vue en `src/Integration.Api/wwwroot` y el gateway inicia los dos servicios como procesos separados.

La primera vez, cree el usuario inicial desde la pantalla de acceso en el equipo local de desarrollo. La contraseña debe tener al menos 10 caracteres. Luego inicie sesión con ese usuario. No se entrega ninguna contraseña predeterminada. La sesión utiliza una cookie HttpOnly; la contraseña se conserva como hash en `users.db`.

En un servidor, configure `ADMIN_USERNAME` y `ADMIN_PASSWORD` antes del primer arranque. El README principal describe la configuración y el despliegue.

## Gestionar datos

El menú ofrece Clientes, Artículos, Vendedores y Ventas. Cada gestión permite crear, consultar, editar y eliminar registros. Clientes y Vendedores incluyen estado activo o inactivo; Artículos añade existencias al precio y estado solicitados.

El precio se introduce en centavos enteros de DOP: `150000` representa RD$ 1,500.00. La comisión se introduce como porcentaje, por ejemplo `12.35`, con hasta dos decimales. Un artículo inactivo no se puede vender. Los artículos con historial de reservas no pueden eliminarse; las reservas activas también impiden modificar su stock.

En Ventas, una operación pendiente se mantiene en el historial mientras el servicio la reintenta. Una venta Pendiente se puede cancelar. Las referencias evitan descuentos duplicados.

## Desarrollo del cliente con Vite

Para obtener recarga automática, inicie primero la API con el comando anterior. En otra terminal:

```powershell
cd web-client
npm install
npm run dev
```

Abra http://localhost:5173. Vite reenvía `/api` y `/health` al puerto HTTP local de la API `62703`, de modo que la sesión mantiene el mismo origen en el navegador. Si usa otro puerto, configure `VITE_DEV_API_URL` antes de iniciar Vite o cópielo desde `.env.example` a `.env.local`. No coloque contraseñas ni claves en variables `VITE_*`.

## Pruebas y publicación

```powershell
npm test
npm run build
```

Desde la raíz ejecute `dotnet test Integracion.sln` y publique con:

```powershell
dotnet publish src/Integration.Api/Integration.Api.csproj -c Release -o artifacts/publish
```

La publicación incluye la web, el gateway y los dos servicios en `services/`. El servidor necesita el runtime ASP.NET Core 8; Node.js solo se necesita para compilar. Para verificar el paquete con procesos y bases temporales, ejecute `./scripts/Test-PublishedApp.ps1` desde la raíz.

El usuario inicial dispone de la opción **Usuarios** en el menú para crear cuentas adicionales. Las cuentas nuevas pueden utilizar las gestiones, pero no crear otras cuentas. Esta entrega todavía no incluye cambio de contraseña, desactivación ni recuperación de cuentas.
