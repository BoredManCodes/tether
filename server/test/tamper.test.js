import test from 'node:test';
import assert from 'node:assert/strict';
import { tamperMessages } from '../src/alerts.js';

test('pc tamper messages name the Windows service and helper', () => {
  const m = tamperMessages('pc');
  assert.equal(m.admin_active, 'Tether service was stopped');
  assert.equal(m.accessibility_active, 'Tether helper was stopped');
});

test('phone tamper messages are unchanged', () => {
  const m = tamperMessages('phone');
  assert.equal(m.admin_active, 'Device Owner / admin was turned off');
  assert.equal(m.accessibility_active, 'Accessibility service was turned off');
  assert.equal(m.notif_listener_active, 'Notification access was turned off');
});
