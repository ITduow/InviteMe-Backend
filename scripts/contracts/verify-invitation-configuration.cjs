// Contract verification only. Does not connect to PostgreSQL or call API/providers.
// Supply an installed Ajv 2020 module path as the first argument when not on NODE_PATH.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const Ajv = require(process.argv[2] || 'ajv/dist/2020');
const base = path.resolve(__dirname, '../../docs/specs');
const schema = JSON.parse(fs.readFileSync(path.join(base, 'invitation-configuration.schema.json'), 'utf8'));
const sample = JSON.parse(fs.readFileSync(path.join(base, 'sample-invitation-configuration.json'), 'utf8'));
const ajv = new Ajv({ allErrors: true, strict: true });
ajv.addFormat('uuid', /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
// Structural timestamp check; application still validates date semantics and timezone.
ajv.addFormat('date-time', value => /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(value) && Number.isFinite(Date.parse(value)));
const validate = ajv.compile(schema);
let count = 0;
function check(name, mutate, expected) {
  const input = structuredClone(sample);
  mutate(input);
  assert.equal(validate(input), expected, `${name}: ${JSON.stringify(validate.errors)}`);
  console.log(`PASS ${name}`);
  count++;
}
check('complete snapshot sample', () => {}, true);
check('incomplete draft content permitted', x => { x.content = {}; x.sections = {}; }, true);
check('unsupported schema version rejected', x => { x.schemaVersion = 2; }, false);
check('unknown configuration key rejected', x => { x.approvedBy = 'client'; }, false);
check('missing template rejected', x => { delete x.template; }, false);
check('invalid template UUID rejected', x => { x.template.id = 'bad-id'; }, false);
check('date without offset rejected', x => { x.content.event.startAt = '2026-11-15T18:00:00'; }, false);
check('wrong field type rejected', x => { x.content.rsvp.enabled = 'true'; }, false);
check('unsupported section rejected', x => { x.sections.script = { visible: true }; }, false);
check('invalid map coordinates rejected', x => { x.sections.map.latitude = 91; }, false);
check('gallery limit enforced', x => { x.sections.gallery.mediaIds = Array.from({length: 21}, (_, i) => `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`); }, false);
check('overlong title rejected', x => { x.content.title = 'x'.repeat(201); }, false);
console.log(`${count} contract checks passed. No lifecycle/tenant/DB enforcement is tested here.`);
