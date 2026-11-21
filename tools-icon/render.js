const { Resvg } = require('@resvg/resvg-js');
const fs = require('fs');
const path = require('path');
const src = path.join(__dirname, '..', 'src', 'P2PKiwi.WinUI3', 'Assets', 'AppIcon.svg');
const outDir = path.join(__dirname, '..', 'src', 'P2PKiwi.WinUI3', 'Assets');
const svg = fs.readFileSync(src);
for (const size of [512, 256, 150, 50, 48, 44, 32, 16]) {
  const r = new Resvg(svg, { fitTo: { mode: 'width', value: size } });
  fs.writeFileSync(path.join(outDir, `AppIcon-${size}.png`), r.render().asPng());
  console.log(size, 'ok');
}
// WinUI manifest names
fs.copyFileSync(path.join(outDir, 'AppIcon-150.png'), path.join(outDir, 'Square150x150Logo.png'));
fs.copyFileSync(path.join(outDir, 'AppIcon-44.png'), path.join(outDir, 'Square44x44Logo.png'));
fs.copyFileSync(path.join(outDir, 'AppIcon-50.png'), path.join(outDir, 'StoreLogo.png'));
console.log('manifest copies ok');
