<script setup>
import { reactive, ref } from 'vue'

const props = defineProps({ call: { type: Function, required: true } })
const form = reactive({ username: '', password: '' })
const busy = ref(false)
const error = ref('')
const message = ref('')
async function save() {
  if (busy.value) return
  busy.value = true
  error.value = ''
  message.value = ''
  try {
    const user = await props.call('/api/auth/users', { method: 'POST', body: { ...form } })
    message.value = `Se creó el usuario ${user.username}.`
    form.username = ''
    form.password = ''
  } catch (failure) { error.value = failure.message }
  finally { busy.value = false }
}
</script>

<template>
  <section class="card form-card user-panel">
    <p class="eyebrow">CONTROL DE ACCESO</p><h2>Crear usuario</h2>
    <p>La nueva cuenta podrá iniciar sesión y utilizar las gestiones.</p>
    <p v-if="error" class="alert error" role="alert">{{ error }}</p>
    <p v-if="message" class="alert success" role="status">{{ message }}</p>
    <form @submit.prevent="save"><fieldset class="stack" :disabled="busy">
      <label>Usuario<input v-model.trim="form.username" autocomplete="off" minlength="3" maxlength="50" required /></label>
      <label>Contraseña<input v-model="form.password" type="password" autocomplete="new-password" minlength="10" maxlength="200" required /><small>Usa al menos 10 caracteres.</small></label>
      <button class="primary" type="submit">Crear usuario</button>
    </fieldset></form>
  </section>
</template>
