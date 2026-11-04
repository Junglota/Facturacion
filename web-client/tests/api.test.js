import { test } from 'node:test'
import assert from 'node:assert/strict'
import { request } from '../src/api.js'

test('envía JSON y clave al endpoint correcto y permite inspeccionar la respuesta', async t => {
  let sent
  t.mock.method(globalThis, 'fetch', async (url, options) => {
    sent = { url, options }
    return new Response(JSON.stringify({ id: 1, totalCents: 450000 }), { status: 201 })
  })
  let log
  const data = await request('https://localhost:62702/', 'demo', '/api/v1/sales', {
    method: 'POST', body: { productId: 1, quantity: 3, reference: 'V-001' }, onResponse: value => { log = value },
  })
  assert.equal(sent.url, 'https://localhost:62702/api/v1/sales')
  assert.equal(sent.options.headers['X-API-Key'], 'demo')
  assert.equal(sent.options.headers['Content-Type'], 'application/json')
  assert.deepEqual(JSON.parse(sent.options.body), { productId: 1, quantity: 3, reference: 'V-001' })
  assert.equal(data.totalCents, 450000)
  assert.equal(log.status, 201)
})

test('acepta DELETE 204 sin intentar leer un JSON inexistente', async t => {
  t.mock.method(globalThis, 'fetch', async () => new Response(null, { status: 204 }))
  assert.equal(await request('https://localhost:62702', 'demo', '/api/v1/products/1', { method: 'DELETE' }), null)
})

test('muestra conflictos y conserva la respuesta de error para inspección', async t => {
  t.mock.method(globalThis, 'fetch', async () => new Response(JSON.stringify({ error: { code: 'conflict', message: 'Existencias insuficientes' } }), { status: 409 }))
  let log
  await assert.rejects(request('https://localhost:62702', 'demo', '/api/v1/sales', { onResponse: value => { log = value } }), /HTTP 409: Existencias insuficientes/)
  assert.equal(log.ok, false)
})

test('distingue fallos de red y rechaza URLs no HTTP antes de enviar la clave', async t => {
  const mock = t.mock.method(globalThis, 'fetch', async () => { throw new TypeError('Failed to fetch') })
  await assert.rejects(request('https://localhost:62702', 'demo', '/health'), /certificado HTTPS y CORS/)
  await assert.rejects(request('file:///test', 'demo', '/health'), /URL HTTP o HTTPS/)
  assert.equal(mock.mock.callCount(), 1)
})
