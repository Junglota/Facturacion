# Preparación previa al desarrollo

## 1. Análisis de requisitos de negocio

**Problema:** el área de ventas necesita conocer los productos disponibles, mientras que el área de inventario debe recibir las ventas realizadas para mantener las existencias actualizadas. Copiar información manualmente produce desajustes de stock y registros duplicados.

**Objetivo:** integrar dos aplicaciones bajo control del desarrollador: Inventario y Ventas. El dominio elegido es una tienda de artículos informáticos. Se supone una moneda única, pesos dominicanos (DOP), y una venta de un solo producto por operación.

**Usuarios previstos:** encargado de inventario y vendedor. Inventario crea, consulta, modifica y elimina productos sin historial de ventas. Ventas consulta el catálogo, registra operaciones y consulta su historial. Ambos acceden a la misma información mediante la API.

**Escenario principal:** Inventario registra 10 teclados de RD$ 1,500.00. Ventas consulta el catálogo y vende 3 teclados. La API registra la venta de RD$ 4,500.00 y actualiza el stock a 7. Inventario consulta las existencias y las ventas recibidas.

El enunciado no identifica organizaciones ni interlocutores reales. Este documento establece supuestos del caso académico; no afirma que se hayan realizado entrevistas. En un proyecto real, estos requisitos se revisarían con el encargado de inventario y el responsable de ventas antes del desarrollo.

| Requisito | Criterio de aceptación |
|---|---|
| Compartir productos | Un producto creado desde Inventario aparece en el catálogo de Ventas. |
| Registrar ventas | Una venta válida devuelve 201, total calculado y stock descontado. |
| Evitar sobreventa | Si no hay stock, devuelve 409 y no registra ni descuenta nada. |
| Evitar duplicados | La misma referencia con los mismos datos devuelve la operación original sin otro descuento; con otros datos devuelve 409. |
| Conservar datos | Productos y ventas se guardan en bases SQLite separadas y sobreviven al reinicio de cada servicio. |
| Conservar historial | No se elimina un producto que tenga ventas. Cambiar el precio no altera los totales históricos. |
| Validar entrada | JSON incorrecto o incompleto devuelve 400; valores de negocio inválidos, 422. |
| Controlar acceso | Las rutas de negocio requieren `X-API-Key`; ausencia o clave incorrecta devuelve 401. |
| Documentar y probar | Swagger/OpenAPI disponible y pruebas automáticas de integración. |

**Alcance actual:** gateway con sesión de usuario, clientes de consola, aplicación Vue, CRUD de clientes, artículos y vendedores, registro/consulta de ventas y persistencia local. El usuario inicial puede crear cuentas adicionales. La desactivación o recuperación de cuentas, la administración de roles, las facturas completas y la interfaz contable siguen fuera de esta entrega. Tampoco incluye devoluciones, impuestos, múltiples monedas ni pedidos de varios productos.

## 2. Semántica del dominio

| Concepto | Definición y reglas |
|---|---|
| Producto | Artículo identificado por un ID numérico generado por la API, nombre, precio y existencias. |
| `name` | Nombre de 1 a 100 caracteres después de eliminar espacios iniciales/finales. |
| `priceCents` | Precio unitario en centavos de DOP; entero entre 1 y 1,000,000,000. Evita errores de redondeo. |
| `stock` | Cantidad disponible; entero de 0 a 2,147,483,647. Una actualización de producto reemplaza el conteo absoluto. |
| Venta | Operación confirmada de un producto y cantidad positiva. No se edita ni elimina en esta práctica. |
| `quantity` | Unidades vendidas; entero positivo de hasta 2,147,483,647. Debe haber stock suficiente. |
| `reference` | Identificador único de la operación, de 1 a 100 caracteres; permite detectar reenvíos. Se recortan espacios externos. |
| `totalCents` | Precio vigente al vender multiplicado por cantidad; queda fijo en el historial. |

El servidor genera IDs y totales: los clientes no pueden enviarlos como campos adicionales. En la versión de microservicios, Inventario confirma la reserva y el descuento en una transacción local; Ventas conserva el estado de la operación en otra base. El protocolo de reserva y confirmación permite recuperar operaciones pendientes sin duplicar descuentos. Los rechazos de negocio también quedan registrados en el historial de Ventas.

## 3. Evaluación de arquitectura técnica

| Alternativa | Evaluación para este caso |
|---|---|
| REST sobre HTTP/JSON | Operaciones claras, integración directa con `HttpClient`, fácil inspección y documentación OpenAPI. Seleccionada. |
| GraphQL | Útil para consultas variables y relaciones complejas; este catálogo e historial no requieren esa flexibilidad. |
| gRPC | Útil para comunicación interna fuertemente tipada y alto rendimiento; añadir contratos Protobuf no aporta una ventaja necesaria en esta práctica. |

**Decisión actualizada:** dos microservicios ASP.NET Core Minimal API en C# sobre .NET 8, Inventario y Ventas, con una base SQLite propia por servicio. Un gateway conserva la dirección pública y sirve Vue; los dos ejecutables de consola siguen consumiendo REST. La comunicación entre servicios usa HTTP y `HttpClient`. Consultas SQL parametrizadas, restricciones de integridad, referencias únicas y recuperación de estados pendientes protegen la consistencia. El diseño completo se encuentra en `DISENO.md`.

## 4. Estándares y directrices

- Recursos en plural y minúsculas: `products`, `sales`.
- Versionado en URL: `/api/v1`. Cambios incompatibles requieren una nueva versión.
- Campos JSON en `camelCase`; C# utiliza `PascalCase` internamente.
- URL con recurso e ID; sin verbos CRUD ni anidamiento innecesario.
- GET consulta, POST crea, PUT reemplaza un producto y DELETE elimina un producto sin ventas.
- Listas con `offset` (desde 0) y `limit` (1–100; predeterminado 100), ordenadas por ID.
- Estados: 200 consulta/actualización, 201 creación, 204 eliminación, 400 formato inválido, 401 autenticación, 404 ausencia, 409 conflicto, 422 regla de entrada y 503 base ocupada.
- Errores JSON uniformes: `{"error":{"code":"conflict","message":"Existencias insuficientes"}}`.
- Acceso de usuarios mediante cookie de sesión. La cabecera `X-API-Key` es opcional y debe configurarse para integraciones; las llamadas entre servicios usan una clave distinta.
- Documentación OpenAPI generada desde la implementación y Swagger UI para prototipar solicitudes antes de integrar los clientes.
- Pruebas con bases aisladas para verificar efectos de negocio y transacciones.
