export async function request(baseUrl, apiKey, path, { method = 'GET', body, onResponse } = {}) {
  const base = new URL(baseUrl)
  if (!['http:', 'https:'].includes(base.protocol) || base.username || base.password || base.search || base.hash)
    throw new Error('Indica una URL HTTP o HTTPS de la API, sin credenciales, consultas ni fragmentos.')
  const url = base.href.replace(/\/+$/, '') + path
  const started = Date.now()
  let response
  try {
    response = await fetch(url, {
      method,
      headers: { ...(apiKey ? { 'X-API-Key': apiKey } : {}), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...(method === 'GET' ? {} : { 'X-Requested-With': 'XMLHttpRequest' }) },
      credentials: 'include',
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      signal: AbortSignal.timeout(15000),
    })
  } catch (error) {
    const message = error.name === 'TimeoutError'
      ? 'La API no respondió en 15 segundos.'
      : 'No se pudo conectar. Comprueba la URL, que la API esté iniciada, el certificado HTTPS y CORS.'
    onResponse?.({ method, path, status: 'RED', ok: false, duration: Date.now() - started, body: { error: message } })
    throw new Error(message)
  }
  const text = await response.text()
  let data = null
  try { data = text ? JSON.parse(text) : null } catch { data = text }
  onResponse?.({ method, path, status: response.status, ok: response.ok, duration: Date.now() - started, body: data })
  if (!response.ok) throw new Error(`HTTP ${response.status}: ${data?.error?.message ?? response.statusText}`)
  if (text && typeof data === 'string') throw new Error('La respuesta no es JSON. Comprueba que la URL corresponde a la API.')
  return data
}
