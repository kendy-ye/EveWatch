// 从 EVE SDE 的 CSV 生成嵌入式星系图资源。
// 用法：node tools/build-galaxy-data.js <SDE CSV 目录> <输出 galaxy.json.gz>
// 数据源：mapSolarSystems.csv / mapSolarSystemJumps.csv / mapRegions.csv / mapConstellations.csv

const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const inputDir = process.argv[2];
const outputFile = process.argv[3] || path.join(__dirname, "..", "src", "EveWatch.Core", "Data", "galaxy.json.gz");

if (!inputDir) {
  console.error("用法: node tools/build-galaxy-data.js <SDE CSV 目录> [输出文件]");
  process.exit(1);
}

function parseCsv(text) {
  const rows = [];
  let row = [];
  let field = "";
  let quoted = false;

  for (let i = 0; i < text.length; i++) {
    const c = text[i];

    if (quoted) {
      if (c === '"') {
        if (text[i + 1] === '"') {
          field += '"';
          i++;
        } else {
          quoted = false;
        }
      } else {
        field += c;
      }
      continue;
    }

    if (c === '"') {
      quoted = true;
    } else if (c === ",") {
      row.push(field);
      field = "";
    } else if (c === "\n") {
      row.push(field);
      rows.push(row);
      row = [];
      field = "";
    } else if (c !== "\r") {
      field += c;
    }
  }

  if (field.length > 0 || row.length > 0) {
    row.push(field);
    rows.push(row);
  }

  return rows;
}

function loadCsv(file) {
  const text = fs.readFileSync(path.join(inputDir, file), "utf8").replace(/^\uFEFF/, "");
  const rows = parseCsv(text).filter((row) => row.length > 1);
  const headers = rows[0].map((header) => header.trim());

  return rows.slice(1).map((row) => {
    const item = {};
    headers.forEach((header, index) => {
      item[header] = (row[index] ?? "").trim();
    });
    return item;
  });
}

const systemRows = loadCsv("mapSolarSystems.csv");
const jumpRows = loadCsv("mapSolarSystemJumps.csv");
const regionRows = loadCsv("mapRegions.csv");
const constellationRows = loadCsv("mapConstellations.csv");

const regions = regionRows.map((row) => [Number(row.regionID), row.regionName]);
const constellations = constellationRows.map((row) => [Number(row.constellationID), row.constellationName]);

// 坐标只用于相对布局：按 1e13 缩放并保留 3 位小数，体积可压掉一半以上。
// GalaxyMap 暴露的 X/Y/Z 是缩放后的值，coordinateScale 记录倍率。
const COORDINATE_SCALE = 1e13;

const systems = systemRows.map((row) => [
  Number(row.solarSystemID),
  row.solarSystemName,
  Number(row.security),
  Number(row.regionID),
  Number(row.constellationID),
  Number((Number(row.x) / COORDINATE_SCALE).toFixed(3)),
  Number((Number(row.y) / COORDINATE_SCALE).toFixed(3)),
  Number((Number(row.z) / COORDINATE_SCALE).toFixed(3)),
]);

const edgeSet = new Set();
for (const row of jumpRows) {
  const a = Number(row.fromSolarSystemID);
  const b = Number(row.toSolarSystemID);
  if (!Number.isFinite(a) || !Number.isFinite(b) || a === b) {
    continue;
  }
  edgeSet.add(a < b ? `${a}-${b}` : `${b}-${a}`);
}

const edges = [...edgeSet].map((key) => key.split("-").map(Number));

const payload = {
  version: 1,
  generatedAt: new Date().toISOString().slice(0, 10),
  coordinateScale: COORDINATE_SCALE,
  regions,
  constellations,
  systems,
  edges,
};

const json = JSON.stringify(payload);
const compressed = zlib.gzipSync(Buffer.from(json));

fs.mkdirSync(path.dirname(outputFile), { recursive: true });
fs.writeFileSync(outputFile, compressed);

console.log(`星系 ${systems.length}，连接 ${edges.length}`);
console.log(`JSON ${(Buffer.byteLength(json) / 1024).toFixed(0)} KiB，gzip ${(compressed.length / 1024).toFixed(0)} KiB`);
console.log(`写入 ${outputFile}`);
