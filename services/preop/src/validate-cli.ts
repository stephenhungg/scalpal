import { validateCatalog } from "./validate.js";

const errors = validateCatalog();
if (errors.length) {
  console.error(`${errors.length} catalog error(s):`);
  for (const e of errors) console.error(`  - ${e}`);
  process.exit(1);
}
console.log("catalog ok: every anatomy, instrument, port, and step reference resolves");
