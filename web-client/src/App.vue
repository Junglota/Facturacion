<script setup>
import { computed, onMounted, reactive, ref } from 'vue'
import { request } from './api.js'
import CatalogPanel from './CatalogPanel.vue'
import UserPanel from './UserPanel.vue'

const baseUrl = ref(import.meta.env.VITE_API_URL || window.location.origin)
const apiKey = ref('')
const username = ref('')
const isAdmin = ref(false)
const initialized = ref(true)
const checkingAuth = ref(true)
const credentials = reactive({ username: '', password: '' })
const connected = ref(false)
const busy = ref(false)
const tab = ref('home')
const products = ref([])
const sales = ref([])
const productOffset = ref(0)
const saleOffset = ref(0)
const pageSize = 10
const editingId = ref(null)
const product = reactive({ name: '', priceCents: 10000, stock: 10, isActive: true })
const sale = reactive({ productId: '', quantity: 1, reference: '' })
const notice = ref('')
const error = ref('')
const history = ref([])
const selectedProduct = computed(() => products.value.find(p => p.id === Number(sale.productId)))
const currency = new Intl.NumberFormat('es-DO', { style: 'currency', currency: 'DOP' })
const money = cents => currency.format(cents / 100)
const statusLabels = { Pending: 'Pendiente', Confirming: 'Confirmando', Confirmed: 'Confirmada', Rejected: 'Rechazada', Cancelling: 'Cancelando', Cancelled: 'Cancelada' }

async function call(path, options = {}) {
  try { return await request(baseUrl.value, apiKey.value, path, {
    ...options,
    onResponse: response => { history.value = [response, ...history.value].slice(0, 20) },
  }) } catch (failure) {
    if (failure.message.startsWith('HTTP 401') && !['/api/auth/login', '/api/auth/setup', '/api/auth/status'].includes(path)) clearSession()
    throw failure
  }
}

function clearSession() {
  username.value = ''
  isAdmin.value = false
  connected.value = false
  products.value = []
  sales.value = []
  resetProduct()
  Object.assign(sale, { productId: '', quantity: 1, reference: '' })
  productOffset.value = 0
  saleOffset.value = 0
  history.value = []
  tab.value = 'home'
}
async function checkSession() {
  checkingAuth.value = true
  error.value = ''
  try {
    initialized.value = (await call('/api/auth/status')).initialized
    if (initialized.value) {
      try {
        const user = await call('/api/auth/me')
        username.value = user.username
        isAdmin.value = user.isAdmin
        connected.value = true
        await Promise.all([loadProducts(0), loadSales(0)])
      } catch (failure) {
        if (!failure.message.startsWith('HTTP 401')) throw failure
      }
    }
  } catch (failure) { error.value = failure.message }
  finally { checkingAuth.value = false }
}
onMounted(checkSession)
function login() {
  run(async () => {
    if (!initialized.value) {
      await call('/api/auth/setup', { method: 'POST', body: { ...credentials } })
      initialized.value = true
    }
    const user = await call('/api/auth/login', { method: 'POST', body: { ...credentials } })
    credentials.password = ''
    username.value = user.username
    isAdmin.value = user.isAdmin
    connected.value = true
    await Promise.all([loadProducts(0), loadSales(0)])
  })
}
function logout() {
  run(async () => {
    await call('/api/auth/logout', { method: 'POST' })
    clearSession()
    credentials.password = ''
    notice.value = 'Sesión cerrada.'
  })
}

async function run(action) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  notice.value = ''
  try { await action() } catch (failure) { error.value = failure.message }
  finally { busy.value = false }
}

async function loadProducts(offset = productOffset.value) {
  const data = await call(`/api/v1/products?offset=${offset}&limit=${pageSize}`)
  products.value = data
  productOffset.value = offset
}
async function loadSales(offset = saleOffset.value) {
  const data = await call(`/api/v1/sales?offset=${offset}&limit=${pageSize}`)
  sales.value = data
  saleOffset.value = offset
}
function resetProduct() {
  editingId.value = null
  Object.assign(product, { name: '', priceCents: 10000, stock: 10, isActive: true })
}
function editProduct(id) {
  run(async () => {
    const data = await call(`/api/v1/products/${id}`)
    editingId.value = data.id
    Object.assign(product, { name: data.name, priceCents: data.priceCents, stock: data.stock, isActive: data.isActive })
    document.getElementById('product-name')?.focus()
  })
}
function saveProduct() {
  run(async () => {
    const id = editingId.value
    await call(`/api/v1/products${id === null ? '' : `/${id}`}`, {
      method: id === null ? 'POST' : 'PUT', body: { ...product },
    })
    resetProduct()
    notice.value = id === null ? 'Producto creado.' : 'Producto actualizado.'
    try { await loadProducts(id === null ? 0 : productOffset.value) }
    catch (failure) { throw new Error(`El producto se guardó, pero no se pudo refrescar la lista. ${failure.message}`) }
  })
}
function deleteProduct(item) {
  if (!window.confirm(`¿Eliminar «${item.name}»? Si tiene ventas, la API impedirá la eliminación.`)) return
  run(async () => {
    await call(`/api/v1/products/${item.id}`, { method: 'DELETE' })
    if (editingId.value === item.id) resetProduct()
    if (Number(sale.productId) === item.id) sale.productId = ''
    notice.value = 'Producto eliminado.'
    try { await loadProducts(products.value.length === 1 ? Math.max(0, productOffset.value - pageSize) : productOffset.value) }
    catch (failure) { throw new Error(`El producto se eliminó, pero no se pudo refrescar la lista. ${failure.message}`) }
  })
}
function saveSale() {
  run(async () => {
    const result = await call('/api/v1/sales', { method: 'POST', body: { ...sale, productId: Number(sale.productId) } })
    notice.value = result.status === 'Confirmed'
      ? `Venta #${result.id} confirmada por ${money(result.totalCents)}. Existencias actualizadas.`
      : `Venta #${result.id} guardada como ${statusLabels[result.status] || result.status}. El servicio reintentará automáticamente; conserva su referencia.`
    sale.reference = ''
    sale.quantity = 1
    try { await loadProducts(); await loadSales(0) }
    catch (failure) { throw new Error(`La venta se guardó. Consulta su estado antes de crear otra: falló la actualización de las listas. ${failure.message}`) }
  })
}
function cancelSale(item) {
  run(async () => {
    const result = await call(`/api/v1/sales/${item.id}/cancel`, { method: 'POST' })
    notice.value = `Venta #${result.id}: ${statusLabels[result.status] || result.status}.`
    await loadProducts()
    await loadSales()
  })
}
</script>

<template>
  <header class="topbar">
    <a class="brand" href="#" @click="tab = 'home'"><span class="brand-icon" aria-hidden="true">↔</span> Facturación <span class="muted">/</span> Gestión</a>
    <div v-if="username" class="user-actions"><span>{{ username }}</span><button type="button" :disabled="busy" @click="logout">Cerrar sesión</button></div>
  </header>
  <main v-if="checkingAuth" class="auth-main"><p role="status">Comprobando sesión…</p></main>
  <main v-else-if="!username" class="auth-main">
    <section class="card form-card auth-card">
      <p class="eyebrow">ACCESO AL SISTEMA</p>
      <h1>{{ initialized ? 'Iniciar sesión' : 'Configurar usuario inicial' }}</h1>
      <p>{{ initialized ? 'Introduce tus credenciales para continuar.' : 'Crea el primer usuario desde este equipo.' }}</p>
      <p v-if="error" class="alert error" role="alert">{{ error }}</p>
      <p v-if="notice" class="alert success" role="status">{{ notice }}</p>
      <button v-if="error && error.includes('No se pudo conectar')" type="button" @click="checkSession">Reintentar conexión</button>
      <form @submit.prevent="login"><fieldset class="stack" :disabled="busy">
        <label>Usuario<input v-model.trim="credentials.username" autocomplete="username" minlength="3" maxlength="50" required /></label>
        <label>Contraseña<input v-model="credentials.password" type="password" :autocomplete="initialized ? 'current-password' : 'new-password'" minlength="10" maxlength="200" required /><small v-if="!initialized">Usa al menos 10 caracteres.</small></label>
        <button class="primary" type="submit">{{ busy ? 'Procesando…' : initialized ? 'Entrar' : 'Crear usuario y entrar' }}</button>
      </fieldset></form>
    </section>
  </main>
  <main v-else>
    <p v-if="error" class="alert error" role="alert">{{ error }}</p>
    <p v-if="notice" class="alert success" role="status">{{ notice }}</p>
    <nav class="tabs" aria-label="Secciones">
      <button :class="{ active: tab === 'home' }" :aria-pressed="tab === 'home'" @click="tab = 'home'">Inicio</button>
      <button :class="{ active: tab === 'clients' }" :aria-pressed="tab === 'clients'" @click="tab = 'clients'">Clientes</button>
      <button :class="{ active: tab === 'products' }" :aria-pressed="tab === 'products'" @click="tab = 'products'">Artículos</button>
      <button :class="{ active: tab === 'sellers' }" :aria-pressed="tab === 'sellers'" @click="tab = 'sellers'">Vendedores</button>
      <button :class="{ active: tab === 'sales' }" :aria-pressed="tab === 'sales'" @click="tab = 'sales'">Ventas</button>
      <button v-if="isAdmin" :class="{ active: tab === 'users' }" :aria-pressed="tab === 'users'" @click="tab = 'users'">Usuarios</button>
    </nav>

    <section v-if="tab === 'home'" class="card home-panel">
      <p class="eyebrow">MENÚ PRINCIPAL</p><h1>Gestiones</h1>
      <p>Selecciona una gestión para consultar o actualizar sus registros.</p>
      <div class="home-links">
        <button @click="tab = 'clients'"><strong>Clientes</strong><span>Razón social, identificación y cuenta contable</span><b aria-hidden="true">→</b></button>
        <button @click="tab = 'products'"><strong>Artículos</strong><span>Descripción, precio, estado y existencias</span><b aria-hidden="true">→</b></button>
        <button @click="tab = 'sellers'"><strong>Vendedores</strong><span>Nombre, comisión y estado</span><b aria-hidden="true">→</b></button>
        <button @click="tab = 'sales'"><strong>Ventas</strong><span>Operaciones e historial</span><b aria-hidden="true">→</b></button>
        <button v-if="isAdmin" @click="tab = 'users'"><strong>Usuarios</strong><span>Crear cuentas de acceso</span><b aria-hidden="true">→</b></button>
      </div>
    </section>
    <CatalogPanel v-else-if="tab === 'clients' || tab === 'sellers'" :key="tab" :kind="tab" :call="call" />
    <UserPanel v-else-if="tab === 'users' && isAdmin" :call="call" />
    <div v-else-if="tab === 'products'" class="workspace">
      <section class="card form-card">
        <p class="eyebrow">INVENTARIO</p><h2>{{ editingId === null ? 'Nuevo artículo' : `Editar artículo #${editingId}` }}</h2>
        <form @submit.prevent="saveProduct">
          <fieldset class="stack" :disabled="busy || !connected">
            <label>Descripción<input id="product-name" v-model.trim="product.name" required maxlength="100" placeholder="Ej. Teclado mecánico" /></label>
            <label>Precio unitario en centavos (DOP)<input v-model.number="product.priceCents" type="number" min="1" max="1000000000" step="1" required /><small>150000 centavos = RD$ 1,500.00</small></label>
            <label>Existencias<input v-model.number="product.stock" type="number" min="0" max="2147483647" step="1" required /></label>
            <label>Estado<select v-model="product.isActive"><option :value="true">Activo</option><option :value="false">Inactivo</option></select></label>
            <p v-if="editingId !== null" class="hint">Al guardar se reemplaza el stock por este conteo absoluto.</p>
            <button class="primary" type="submit">{{ editingId === null ? 'Crear artículo' : 'Guardar cambios' }}</button>
            <button v-if="editingId !== null" type="button" @click="resetProduct">Cancelar edición</button>
          </fieldset>
        </form>
      </section>
      <section class="card list-card">
        <div class="section-heading"><h2>Artículos</h2><button type="button" :disabled="busy || !connected" @click="run(() => loadProducts())">Actualizar</button></div>
        <div class="table-scroll"><table>
          <thead><tr><th>ID / Descripción</th><th>Precio</th><th>Stock</th><th>Estado</th><th>Acciones</th></tr></thead>
          <tbody><tr v-for="item in products" :key="item.id"><td><small>#{{ item.id }}</small><strong>{{ item.name }}</strong></td><td>{{ money(item.priceCents) }}</td><td><span class="stock" :class="{ empty: item.stock === 0 }">{{ item.stock }} ud.</span></td><td>{{ item.isActive ? 'Activo' : 'Inactivo' }}</td><td class="actions"><button :disabled="busy || !connected" @click="editProduct(item.id)">Consultar / editar</button><button class="danger" :disabled="busy || !connected" @click="deleteProduct(item)">Eliminar</button></td></tr>
          <tr v-if="!products.length"><td colspan="5" class="empty-state">No hay artículos en esta página. Crea uno para comenzar.</td></tr></tbody>
        </table></div>
        <div class="pagination"><span>Página {{ productOffset / pageSize + 1 }} · Hasta {{ pageSize }} productos</span><div><button :disabled="busy || !connected || productOffset === 0" @click="run(() => loadProducts(productOffset - pageSize))">Anterior</button><button :disabled="busy || !connected || products.length < pageSize" @click="run(() => loadProducts(productOffset + pageSize))">Siguiente</button></div></div>
      </section>
    </div>

    <div v-else-if="tab === 'sales'" class="workspace">
      <section class="card form-card">
        <p class="eyebrow">VENTAS</p><h2>Registrar venta</h2><p>La API calcula el total y descuenta las existencias.</p>
        <form @submit.prevent="saveSale"><fieldset class="stack" :disabled="busy || !connected">
          <label>Artículo<select v-model="sale.productId" required><option disabled value="">Selecciona un artículo</option><option v-for="item in products" :key="item.id" :value="item.id" :disabled="!item.isActive">#{{ item.id }} · {{ item.name }} ({{ item.isActive ? `${item.stock} ud.` : 'Inactivo' }})</option></select><small>Se muestran los artículos de la página actual del catálogo.</small></label>
          <label>Cantidad<input v-model.number="sale.quantity" type="number" min="1" max="2147483647" step="1" required /></label>
          <label>Referencia única<input v-model.trim="sale.reference" required maxlength="100" placeholder="Ej. V-001" /><small>Usa la misma referencia para identificar un reenvío.</small></label>
          <div class="total"><span>Total estimado</span><strong>{{ selectedProduct ? money(selectedProduct.priceCents * sale.quantity) : '—' }}</strong></div>
          <button class="primary" type="submit">Registrar venta</button>
        </fieldset></form>
      </section>
      <section class="card list-card">
        <div class="section-heading"><div><h2>Historial de ventas</h2><p>Operaciones confirmadas y totales registrados por la API.</p></div><button :disabled="busy || !connected" @click="run(async () => { await loadSales(); await loadProducts() })">Actualizar</button></div>
        <div class="table-scroll"><table><thead><tr><th>Venta / Referencia</th><th>Producto</th><th>Cantidad</th><th>Total</th><th>Estado</th></tr></thead><tbody>
          <tr v-for="item in sales" :key="item.id"><td><small>#{{ item.id }}</small><strong>{{ item.reference }}</strong></td><td>#{{ item.productId }}</td><td>{{ item.quantity }} ud.</td><td>{{ item.totalCents === null ? '—' : money(item.totalCents) }}</td><td><span class="stock" :class="{ empty: item.status !== 'Confirmed' }">{{ statusLabels[item.status] || item.status }}</span><small v-if="item.failure">{{ item.failure }}</small><button v-if="item.status === 'Pending'" :disabled="busy || !connected" @click="cancelSale(item)">Cancelar</button></td></tr>
          <tr v-if="!sales.length"><td colspan="5" class="empty-state">{{ connected ? 'Todavía no hay ventas en esta página.' : 'Conecta la API para ver las ventas.' }}</td></tr>
        </tbody></table></div>
        <div class="pagination"><span>Página {{ saleOffset / pageSize + 1 }} · Hasta {{ pageSize }} ventas</span><div><button :disabled="busy || !connected || saleOffset === 0" @click="run(() => loadSales(saleOffset - pageSize))">Anterior</button><button :disabled="busy || !connected || sales.length < pageSize" @click="run(() => loadSales(saleOffset + pageSize))">Siguiente</button></div></div>
      </section>
    </div>

    <section class="card request-log">
      <div class="section-heading"><div><h2>Registro de solicitudes</h2><p>Últimas 20 respuestas. Abre una solicitud para inspeccionar el JSON.</p></div><button :disabled="!history.length" @click="history = []">Limpiar</button></div>
      <p v-if="!history.length" class="log-empty">Las solicitudes aparecerán aquí al conectar la API.</p>
      <details v-for="(entry, index) in history" :key="history.length + '-' + index + '-' + entry.path + '-' + entry.duration">
        <summary><span class="method">{{ entry.method }}</span><code>{{ entry.path }}</code><span class="http-status" :class="{ failed: !entry.ok }">{{ entry.status }}</span><small>{{ entry.duration }} ms</small></summary>
        <pre>{{ JSON.stringify(entry.body, null, 2) ?? '(sin cuerpo)' }}</pre>
      </details>
    </section>
    <footer>Inventario / Ventas <span>Cliente de pruebas · API v1</span></footer>
  </main>
</template>
