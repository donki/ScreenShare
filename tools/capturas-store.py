"""Capturas de la Microsoft Store (1600x900): cada captura de la interfaz, sin la barra de titulo de
la prueba (lleva [SOC_SANDBOX]), centrada sobre el degradado indigo del icono.
Uso: python tools/capturas-store.py ORIGEN:RECORTE_ARRIBA ... (deja store/microsoft/capturas/NN.png)
"""
import os
import sys
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
W, H = 1600, 900


def fondo():
    a, b = (0x63, 0x5B, 0xF2), (0x2A, 0x1C, 0xB8)
    img = Image.new("RGB", (W, H))
    px = img.load()
    for y in range(H):
        for x in range(W):
            t = (x / W + y / H) / 2
            px[x, y] = tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))
    return img


def main(args):
    salida = os.path.join(ROOT, "store", "microsoft", "capturas")
    os.makedirs(salida, exist_ok=True)
    base = fondo()
    for n, arg in enumerate(args, 1):
        ruta, _, recorte = arg.rpartition(":")
        cap = Image.open(ruta).convert("RGB")
        cap = cap.crop((0, int(recorte or 0), cap.width, cap.height))
        escala = min((W - 120) / cap.width, (H - 80) / cap.height)
        cap = cap.resize((int(cap.width * escala), int(cap.height * escala)), Image.LANCZOS)
        img = base.copy()
        img.paste(cap, ((W - cap.width) // 2, (H - cap.height) // 2))
        img.save(os.path.join(salida, f"{n:02d}.png"))
    print(f"{len(args)} capturas en {salida}")


if __name__ == "__main__":
    main(sys.argv[1:])
