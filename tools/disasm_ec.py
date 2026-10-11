"""Offline RV32/RVC decoding of a user-supplied EC image. No hardware access."""
import argparse
import hashlib
from pathlib import Path

import capstone


def integer(value: str) -> int:
    return int(value, 0)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path, help="Extracted EC image, not a full SPI dump")
    parser.add_argument("--start", type=integer, default=0xC7F2)
    parser.add_argument("--end", type=integer, default=0xC946)
    parser.add_argument("--output", type=Path, help="Optional local text output")
    args = parser.parse_args()
    data = args.image.read_bytes()
    if not 0 <= args.start < args.end <= len(data):
        parser.error("Require 0 <= start < end <= image length")
    decoder = capstone.Cs(
        capstone.CS_ARCH_RISCV,
        capstone.CS_MODE_RISCV32 | capstone.CS_MODE_RISCVC,
    )
    decoder.detail = True
    decoder.skipdata = True

    def skip_data(code, size, offset, _):
        first = code[offset]
        first = first if isinstance(first, int) else first[0]
        return 4 if offset + 1 < size and first & 3 == 3 else 2

    decoder.skipdata_setup = (".byte", skip_data, None)
    branches = {
        "j", "jal", "beq", "bne", "blt", "bge", "bltu", "bgeu",
        "beqz", "bnez", "blez", "bgez", "bltz", "bgtz",
        "c.j", "c.jal", "c.beqz", "c.bnez",
    }
    rows = ["EC region SHA256: " + hashlib.sha256(data).hexdigest()]
    for instruction in decoder.disasm(data[args.start:args.end], args.start):
        row = (
            f"{instruction.address:06x}: {instruction.bytes.hex():<10} "
            f"{instruction.mnemonic:<12} {instruction.op_str}"
        )
        if instruction.id and instruction.mnemonic in branches:
            target = (instruction.address + instruction.operands[-1].imm) & 0xFFFFFFFF
            row += f" ; target=0x{target:x}"
        rows.append(row)
    rows.append("Verify instruction boundaries and control flow manually; decoded data is not proof of code.")
    output = "\n".join(rows) + "\n"
    if args.output:
        args.output.write_text(output, encoding="utf-8")
    else:
        print(output, end="")


if __name__ == "__main__":
    main()
