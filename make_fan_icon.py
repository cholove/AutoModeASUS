# -*- coding: utf-8 -*-
"""生成 AutoModeASUS 风扇图标：fan.ico（多尺寸 16~256） + fan_preview.png

形状参照示意图：5 片逗号形桨叶（窄尖内指、外端大圆头、整体弯曲扫掠）
+ 中心轮毂环 + 透明中心孔。
静态 ICO 为深炭色（exe 文件图标）；托盘图标由 Program.cs 的 MakeIcon()
运行时按相同几何绘制，并随模式变色：安静=绿 / 标准=橙 / 性能=红。
"""
import math
from PIL import Image, ImageDraw

SIZE = 1024          # 高分辨率绘制后缩放，保证边缘平滑
C = SIZE / 2.0
U = SIZE / 256.0     # 以 256 为基准的设计单位

HUB_R = 36 * U       # 轮毂外圆半径
GAP_R = 45 * U       # 桨叶与轮毂之间透明间隙
HOLE_R = 14 * U      # 中心孔半径
N = 5
COLOR = (38, 36, 40, 255)  # 深炭色

# 桨叶脊柱参数（设计单位）：内尖端 -> 外圆头
R0, R1 = 40.0, 74.0                      # 脊柱半径范围
SWEEP = math.radians(34)                 # 脊柱弯曲角（内指->外头的方位差）
W0, W1 = 13.0, 46.0                      # 半宽：内尖 -> 圆头半径


def polar(r, a):
    return (C + r * U * math.cos(a), C + r * U * math.sin(a))


def blade_poly(base):
    """单片逗号桨叶。脊柱: 半径 R0->R1, 方位角 0->SWEEP; 半宽 W0->W1。
    两侧边界 = 脊柱 ± 半宽(沿切向); 外端以半圆帽封住。base 为整叶旋转角。
    """
    steps = 64
    left, right = [], []
    for i in range(steps + 1):
        t = i / steps
        r = R0 + (R1 - R0) * t
        th = SWEEP * t
        w = W0 + (W1 - W0) * (t ** 1.1)
        px, py = polar(r, th)
        # 切向单位向量（近似垂直于脊柱）
        tx, ty = -math.sin(th), math.cos(th)
        left.append((px + w * U * tx, py + w * U * ty))
        right.append((px - w * U * tx, py - w * U * ty))

    # 圆头帽：绕头心从 SWEEP+90° 经过外向 SWEEP 到 SWEEP-90°
    hx, hy = polar(R1, SWEEP)
    cap = []
    for k in range(1, 41):
        a = (math.pi / 2) - math.pi * k / 40.0   # +90° -> -90°
        cap.append((hx + W1 * U * math.cos(SWEEP + a),
                    hy + W1 * U * math.sin(SWEEP + a)))

    pts = left + cap + list(reversed(right))
    # 整体旋转
    out = []
    cb, sb = math.cos(base), math.sin(base)
    for x, y in pts:
        dx, dy = x - C, y - C
        out.append((C + dx * cb - dy * sb, C + dy * cb + dx * sb))
    return out


mask = Image.new("L", (SIZE, SIZE), 0)
d = ImageDraw.Draw(mask)

for i in range(N):
    d.polygon(blade_poly(-math.pi / 2 + i * (2 * math.pi / N)), fill=255)

d.ellipse([C - GAP_R, C - GAP_R, C + GAP_R, C + GAP_R], fill=0)
d.ellipse([C - HUB_R, C - HUB_R, C + HUB_R, C + HUB_R], fill=255)
d.ellipse([C - HOLE_R, C - HOLE_R, C + HOLE_R, C + HOLE_R], fill=0)

solid = Image.new("RGBA", (SIZE, SIZE), COLOR)
img = Image.merge("RGBA", (*solid.split()[:3], mask))
img = img.resize((256, 256), Image.LANCZOS)

img.save(r"D:\Software\AutoModeASUS\fan.ico",
         sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

# 预览：白底大样 + 16px 小样 + 三种模式色小样（模拟托盘）
preview = Image.new("RGBA", (256 + 40 + 3 * 90, 300), (255, 255, 255, 255))
preview.paste(img, (20, 20), img)
tiny = img.resize((16, 16), Image.LANCZOS)
preview.paste(tiny, (20, 280 - 16), tiny)
small = img.resize((64, 64), Image.LANCZOS)
x = 256 + 40
for col in ((0, 170, 60, 255), (255, 140, 0, 255), (220, 40, 40, 255)):
    c_img = Image.new("RGBA", (64, 64), col)
    c_img = Image.merge("RGBA", (*c_img.split()[:3], small.split()[3]))
    preview.paste(c_img, (x, 20), c_img)
    x += 90
preview.save(r"D:\Software\AutoModeASUS\fan_preview.png")
print("OK: fan.ico + fan_preview.png 已生成")
