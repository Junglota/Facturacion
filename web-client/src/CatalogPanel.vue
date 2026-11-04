<script setup>
import { onMounted, reactive, ref } from 'vue'

const props = defineProps({ kind: { type: String, required: true }, call: { type: Function, required: true } })
const isClient = props.kind === 'clients'
const plural = isClient ? 'Clientes' : 'Vendedores'
const singular = isClient ? 'cliente' : 'vendedor'
const records = ref([])
const offset = ref(0)
const limit = 10
const editingId = ref(null)
const busy = ref(false)
const error = ref('')
const message = ref('')
const form = reactive({ name: '', taxId: '', accountingAccount: '', commissionPercent: 0, isActive: true })

function reset() {
  editingId.value = null
  Object.assign(form, { name: '', taxId: '', accountingAccount: '', commissionPercent: 0, isActive: true })
}
async function load(page = offset.value) {
  const data = await props.call(`/api/v1/${props.kind}?offset=${page}&limit=${limit}`)
  records.value = data
  offset.value = page
}
async function perform(action) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  message.value = ''
  try { await action() } catch (failure) { error.value = failure.message }
  finally { busy.value = false }
}
onMounted(() => perform(() => load(0)))
function edit(id) {
  perform(async () => {
    const record = await props.call(`/api/v1/${props.kind}/${id}`)
    editingId.value = id
    Object.assign(form, record)
    document.getElementById(`${props.kind}-name`)?.focus()
  })
}
function save() {
  perform(async () => {
    const id = editingId.value
    const body = isClient
      ? { name: form.name, taxId: form.taxId, accountingAccount: form.accountingAccount, isActive: form.isActive }
      : { name: form.name, commissionPercent: form.commissionPercent, isActive: form.isActive }
    await props.call(`/api/v1/${props.kind}${id === null ? '' : `/${id}`}`, { method: id === null ? 'POST' : 'PUT', body })
    reset()
    message.value = id === null ? `Se creó el ${singular}.` : `Se actualizó el ${singular}.`
    try { await load(id === null ? 0 : offset.value) }
    catch (failure) { throw new Error(`El registro se guardó, pero no se pudo actualizar la lista. ${failure.message}`) }
  })
}
function remove(record) {
  if (!window.confirm(`¿Eliminar ${singular} «${record.name}»?`)) return
  perform(async () => {
    await props.call(`/api/v1/${props.kind}/${record.id}`, { method: 'DELETE' })
    if (editingId.value === record.id) reset()
    message.value = `Se eliminó el ${singular}.`
    try { await load(records.value.length === 1 ? Math.max(0, offset.value - limit) : offset.value) }
    catch (failure) { throw new Error(`El registro se eliminó, pero no se pudo actualizar la lista. ${failure.message}`) }
  })
}
</script>

<template>
  <div>
    <p v-if="error" class="alert error" role="alert">{{ error }}</p>
    <p v-if="message" class="alert success" role="status">{{ message }}</p>
    <div class="workspace">
      <section class="card form-card">
        <p class="eyebrow">{{ plural.toUpperCase() }}</p>
        <h2>{{ editingId === null ? `Nuevo ${singular}` : `Editar ${singular} #${editingId}` }}</h2>
        <form @submit.prevent="save"><fieldset class="stack" :disabled="busy">
          <label>{{ isClient ? 'Nombre comercial o razón social' : 'Nombre' }}<input :id="`${kind}-name`" v-model.trim="form.name" required maxlength="150" /></label>
          <template v-if="isClient">
            <label>RNC o cédula<input v-model.trim="form.taxId" required maxlength="20" /></label>
            <label>Cuenta contable<input v-model.trim="form.accountingAccount" required maxlength="50" /></label>
          </template>
          <label v-else>Porciento de comisión<input v-model.number="form.commissionPercent" type="number" min="0" max="100" step="0.01" required /><small>Porcentaje entre 0 y 100, con hasta dos decimales.</small></label>
          <label>Estado<select v-model="form.isActive"><option :value="true">Activo</option><option :value="false">Inactivo</option></select></label>
          <button class="primary" type="submit">{{ editingId === null ? `Crear ${singular}` : 'Guardar cambios' }}</button>
          <button v-if="editingId !== null" type="button" @click="reset">Cancelar edición</button>
        </fieldset></form>
      </section>
      <section class="card list-card">
        <div class="section-heading"><h2>{{ plural }}</h2><button type="button" :disabled="busy" @click="perform(() => load())">Actualizar</button></div>
        <div class="table-scroll"><table>
          <thead><tr><th>Identificador / Nombre</th><template v-if="isClient"><th>RNC o cédula</th><th>Cuenta contable</th></template><th v-else>Comisión</th><th>Estado</th><th>Acciones</th></tr></thead>
          <tbody>
            <tr v-for="record in records" :key="record.id">
              <td><small>#{{ record.id }}</small><strong>{{ record.name }}</strong></td>
              <template v-if="isClient"><td>{{ record.taxId }}</td><td>{{ record.accountingAccount }}</td></template><td v-else>{{ record.commissionPercent }} %</td>
              <td>{{ record.isActive ? 'Activo' : 'Inactivo' }}</td>
              <td class="actions"><button :disabled="busy" @click="edit(record.id)">Consultar / editar</button><button class="danger" :disabled="busy" @click="remove(record)">Eliminar</button></td>
            </tr>
            <tr v-if="!records.length"><td :colspan="isClient ? 5 : 4" class="empty-state">No hay registros en esta página.</td></tr>
          </tbody>
        </table></div>
        <div class="pagination"><span>Página {{ offset / limit + 1 }}</span><div><button :disabled="busy || offset === 0" @click="perform(() => load(offset - limit))">Anterior</button><button :disabled="busy || records.length < limit" @click="perform(() => load(offset + limit))">Siguiente</button></div></div>
      </section>
    </div>
  </div>
</template>
