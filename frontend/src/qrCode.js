import { qrcodegen } from "./qrcodegen.js";

const QrCode = qrcodegen.QrCode;

export function qrModules(text) {
  const qr = QrCode.encodeText(String(text ?? ""), QrCode.Ecc.MEDIUM);
  const size = qr.size;
  const rows = [];
  for (let y = 0; y < size; y += 1) {
    const row = [];
    for (let x = 0; x < size; x += 1) row.push(qr.getModule(x, y) ? 1 : 0);
    rows.push(row);
  }
  return rows;
}

export function qrPath(modules, quiet = 4) {
  let d = "";
  const size = modules.length;
  for (let y = 0; y < size; y += 1) {
    const row = modules[y];
    for (let x = 0; x < row.length; x += 1) {
      if (row[x]) d += `M${x + quiet},${y + quiet}h1v1h-1z`;
    }
  }
  return { d, size: size + quiet * 2 };
}
