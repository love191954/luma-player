from __future__ import annotations

import math
import struct
from pathlib import Path


SIZES = (16, 24, 32, 48, 64, 128, 256)


def inside_rounded_rect(x: float, y: float, left: float, top: float, right: float, bottom: float, radius: float) -> bool:
    nearest_x = min(max(x, left + radius), right - radius)
    nearest_y = min(max(y, top + radius), bottom - radius)
    return (x - nearest_x) ** 2 + (y - nearest_y) ** 2 <= radius ** 2


def render(size: int) -> tuple[bytes, bytes]:
    supersample = 4
    high_size = size * supersample
    pixels: list[tuple[int, int, int, int]] = []
    left = high_size * 0.08
    top = high_size * 0.08
    right = high_size * 0.92
    bottom = high_size * 0.92
    radius = high_size * 0.20
    center = high_size / 2.0
    accent_radius = high_size * 0.285

    for y in range(high_size):
        for x in range(high_size):
            px = x + 0.5
            py = y + 0.5
            if not inside_rounded_rect(px, py, left, top, right, bottom, radius):
                pixels.append((0, 0, 0, 0))
                continue

            vertical = max(0.0, min(1.0, (py - top) / max(1.0, bottom - top)))
            base = int(43 - vertical * 14)
            color = (base, base + 5, base + 14, 255)

            distance = math.hypot(px - center, py - center)
            if distance <= accent_radius:
                color = (242, 108, 76, 255)

                triangle_top = center - high_size * 0.145
                triangle_bottom = center + high_size * 0.145
                triangle_left = center - high_size * 0.075
                triangle_right = center + high_size * 0.115
                if triangle_top <= py <= triangle_bottom:
                    progress = abs(py - center) / max(1.0, triangle_bottom - center)
                    edge = triangle_left + (triangle_right - triangle_left) * progress
                    if triangle_left <= px <= edge:
                        color = (255, 249, 244, 255)

            pixels.append(color)

    rgba = bytearray()
    for y in range(size):
        for x in range(size):
            samples = []
            for sy in range(supersample):
                row = (y * supersample + sy) * high_size
                for sx in range(supersample):
                    samples.append(pixels[row + x * supersample + sx])

            alpha_total = sum(sample[3] for sample in samples)
            alpha = alpha_total // len(samples)
            if alpha_total == 0:
                rgba.extend((0, 0, 0, 0))
            else:
                red = sum(sample[0] * sample[3] for sample in samples) // alpha_total
                green = sum(sample[1] * sample[3] for sample in samples) // alpha_total
                blue = sum(sample[2] * sample[3] for sample in samples) // alpha_total
                rgba.extend((red, green, blue, alpha))

    dib_pixels = bytearray()
    for y in range(size - 1, -1, -1):
        row_start = y * size * 4
        for x in range(size):
            red, green, blue, alpha = rgba[row_start + x * 4:row_start + x * 4 + 4]
            dib_pixels.extend((blue, green, red, alpha))

    mask_row_bytes = ((size + 31) // 32) * 4
    mask = bytearray(mask_row_bytes * size)
    for y in range(size):
        for x in range(size):
            alpha = rgba[(y * size + x) * 4 + 3]
            if alpha < 128:
                mask[y * mask_row_bytes + x // 8] |= 0x80 >> (x % 8)
    for y in range(size // 2):
        top_row = y * mask_row_bytes
        bottom_row = (size - 1 - y) * mask_row_bytes
        mask[top_row:top_row + mask_row_bytes], mask[bottom_row:bottom_row + mask_row_bytes] = (
            mask[bottom_row:bottom_row + mask_row_bytes], mask[top_row:top_row + mask_row_bytes]
        )

    header = struct.pack("<IIIHHIIIIII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    return bytes(header + dib_pixels + mask), bytes(rgba)


def write_icon(output: Path) -> None:
    entries = []
    images = []
    offset = 6 + len(SIZES) * 16
    for size in SIZES:
        image, _ = render(size)
        entries.append(struct.pack(
            "<BBBBHHII",
            0 if size == 256 else size,
            0 if size == 256 else size,
            0,
            0,
            1,
            32,
            len(image),
            offset,
        ))
        images.append(image)
        offset += len(image)

    output.write_bytes(struct.pack("<HHH", 0, 1, len(SIZES)) + b"".join(entries) + b"".join(images))


def main() -> None:
    project_root = Path(__file__).resolve().parents[1]
    output = (project_root / "src" / "luma.ico").resolve()
    if project_root not in output.parents:
        raise RuntimeError(f"Refusing to write outside project: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    write_icon(output)
    print(f"Generated {output} with sizes: {', '.join(str(size) for size in SIZES)}")


if __name__ == "__main__":
    main()
