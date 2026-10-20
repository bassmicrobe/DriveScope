// Optional asset regeneration. Normal application builds use the committed PNG/ICO.
// Requires Node.js and sharp in the development environment.
const path = require('node:path');
const fs = require('node:fs/promises');
const sharp = require('sharp');

const assets = path.resolve(__dirname, '../assets/branding');
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

async function main() {
  const iconSvg = await fs.readFile(path.join(assets, 'drivescope-appicon-dark.svg'));
  const frames = await Promise.all(sizes.map(size => sharp(iconSvg).resize(size, size).png().toBuffer()));
  const header = Buffer.alloc(6 + 16 * frames.length);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(frames.length, 4);
  let offset = header.length;
  frames.forEach((frame, index) => {
    const entry = 6 + 16 * index;
    header[entry] = header[entry + 1] = sizes[index] === 256 ? 0 : sizes[index];
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(frame.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += frame.length;
  });
  await fs.writeFile(path.join(assets, 'drivescope.ico'), Buffer.concat([header, ...frames]));
  await sharp(iconSvg).resize(512, 512).png().toFile(path.join(assets, 'drivescope-appicon.png'));
  await sharp(path.join(assets, 'drivescope-logo-local.svg')).resize({ width: 1444 }).png().toFile(path.join(assets, 'drivescope-logo-local.png'));
  process.stdout.write('Generated application ICO, PNG preview and header PNG from supplied SVGs.\n');
}

main().catch(error => { console.error(error); process.exitCode = 1; });
