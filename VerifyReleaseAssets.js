"use strict";

const fs = require("fs");
const path = require("path");

if (process.argv.length !== 4) {
  process.stderr.write("Usage: node VerifyReleaseAssets.js <input> <packaged-assets>\n");
  process.exit(2);
}

const inputRoot = path.resolve(process.argv[2]);
const assetsRoot = path.resolve(process.argv[3]);

function rows(root, name) {
  return fs.readFileSync(path.join(root, name), "utf8").split(/\r?\n/)
    .filter((line) => line.trim()).map((line) => JSON.parse(line));
}

function assertSame(name, sourceRows, packagedRows, fields) {
  if (sourceRows.length !== packagedRows.length) {
    throw new Error(`${name}: row count changed`);
  }
  for (let index = 0; index < sourceRows.length; index += 1) {
    const keys = Object.keys(packagedRows[index]).sort();
    if (JSON.stringify(keys) !== JSON.stringify(["id", "translation"])) {
      throw new Error(`${name}:${index + 1}: unexpected packaged field`);
    }
    for (const field of fields) {
      if (JSON.stringify(sourceRows[index][field]) !== JSON.stringify(packagedRows[index][field])) {
        throw new Error(`${name}:${index + 1}: ${field} changed`);
      }
    }
  }
}

for (const name of fs.readdirSync(assetsRoot).filter((item) => /^s\d{2}_.+\.jsonl$/i.test(item))) {
  assertSame(name, rows(inputRoot, name), rows(assetsRoot, name), ["id", "translation"]);
}
for (const name of ["songs_all.jsonl", "psw_missing_all.jsonl"]) {
  assertSame(name, rows(inputRoot, name), rows(assetsRoot, name), ["id", "translation"]);
}

const sourceDefault = rows(inputRoot, "default_xex_codex_direct_ko.jsonl")
  .filter((row) => row.status === "translated");
assertSame(
  "default_xex_codex_direct_ko.jsonl",
  sourceDefault,
  rows(assetsRoot, "default_xex_codex_direct_ko.jsonl"),
  ["id", "translation"]
);

for (const name of fs.readdirSync(assetsRoot).filter((item) => /_mail_ko\.jsonl$/i.test(item))) {
  assertSame(name, rows(inputRoot, name), rows(assetsRoot, name), ["id", "translation"]);
}

process.stdout.write("All packaged Korean translations exactly match input.\n");
