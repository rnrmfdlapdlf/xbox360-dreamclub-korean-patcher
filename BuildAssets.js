"use strict";

const fs = require("fs");
const path = require("path");

if (process.argv.length !== 4) {
  process.stderr.write("Usage: node BuildAssets.js <input> <output>\n");
  process.exit(2);
}

const inputRoot = path.resolve(process.argv[2]);
const outputRoot = path.resolve(process.argv[3]);

function readJsonl(filePath) {
  return fs.readFileSync(filePath, "utf8").split(/\r?\n/)
    .filter((line) => line.trim()).map((line) => JSON.parse(line));
}

function writeJsonl(filePath, rows) {
  fs.writeFileSync(filePath, `${rows.map((row) => JSON.stringify(row)).join("\n")}\n`, "utf8");
}

function select(row, names) {
  const result = {};
  for (const name of names) {
    if (Object.prototype.hasOwnProperty.call(row, name)) result[name] = row[name];
  }
  return result;
}

fs.mkdirSync(outputRoot, { recursive: true });
const dialogueFiles = fs.readdirSync(inputRoot)
  .filter((name) => /^s\d{2}_.+\.jsonl$/i.test(name)).sort();

for (const name of dialogueFiles) {
  const rows = readJsonl(path.join(inputRoot, name)).map((row) =>
    select(row, ["id", "translation"]));
  writeJsonl(path.join(outputRoot, name), rows);
}

for (const name of ["songs_all.jsonl", "psw_missing_all.jsonl"]) {
  const rows = readJsonl(path.join(inputRoot, name)).map((row) =>
    select(row, ["id", "translation"]));
  writeJsonl(path.join(outputRoot, name), rows);
}

const defaultRows = readJsonl(path.join(inputRoot, "default_xex_codex_direct_ko.jsonl"))
  .filter((row) => row.status === "translated")
  .map((row) => select(row, ["id", "translation"]));
writeJsonl(path.join(outputRoot, "default_xex_codex_direct_ko.jsonl"), defaultRows);

const mailFiles = fs.readdirSync(inputRoot)
  .filter((name) => /_mail_ko\.jsonl$/i.test(name)).sort();
for (const name of mailFiles) {
  const rows = readJsonl(path.join(inputRoot, name)).map((row) =>
    select(row, ["id", "translation"]));
  writeJsonl(path.join(outputRoot, name), rows);
}

for (const name of fs.readdirSync(outputRoot)) {
  const filePath = path.join(outputRoot, name);
  if (!fs.statSync(filePath).isFile()) continue;
  const text = fs.readFileSync(filePath, "utf8");
  if (/"source(?:Text|Subject|Body)?"\s*:/u.test(text)) {
    throw new Error(`Japanese source field remains in packaged asset: ${name}`);
  }
  for (const row of readJsonl(filePath)) {
    const keys = Object.keys(row).sort();
    if (keys.length !== 2 || keys[0] !== "id" || keys[1] !== "translation") {
      throw new Error(`Unexpected field remains in packaged asset: ${name}`);
    }
  }
}

process.stdout.write(`Packaged ${dialogueFiles.length + mailFiles.length + 3} source-free assets.\n`);
