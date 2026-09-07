# -*- coding: utf-8 -*-
"""生成 AutoModeASUS 绿叶图标：leaf.ico（多尺寸 16~256） + leaf_preview.png"""
from PIL import Image, ImageDraw, ImageOps

SIZE = 256


def bezier(p0, p1, p2, p3, n=100):
    """三次贝塞尔曲线采样点"""
    pts = []
    for i in range(n + 1):
        t = i / n
        mt = 1 - t
        x = mt ** 3 * p0[0] + 3 * mt * mt * t * p1[0] + 3 * mt * t * t * p2[0] + t ** 3 * p3[0]
        y = mt ** 3 * p0[1] + 3 * mt * mt * t * p1[1] + 3 * mt * t * t * p2[1] + t ** 3 * p3[1]
        pts.append((x, y))
    return pts


# ---- 叶子轮廓：水平梭形（叶柄端 -> 上缘 -> 叶尖 -> 下缘 -> 闭合）----
p0 = (40, 128)      # 叶柄端
p1a = (96, 40)      # 上缘近端控制点
p2a = (196, 116)    # 上缘近尖控制点
p2 = (232, 128)     # 叶尖
p2b = (196, 140)    # 下缘近尖控制点
p1b = (96, 216)     # 下缘近端控制点
leaf_pts = bezier(p0, p1a, p2a, p2) + bezier(p2, p2b, p1b, p0)[1:]

# ---- 基底透明图 ----
img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))

# ---- 渐变填充（上浅下深绿）----
grad = Image.linear_gradient("L").resize((SIZE, SIZE))
colored = ImageOps.colorize(grad.convert("L"), (129, 199, 132), (27, 94, 32)).convert("RGBA")
leaf_mask = Image.new("L", (SIZE, SIZE), 0)
ImageDraw.Draw(leaf_mask).polygon(leaf_pts, fill=255)
img = Image.composite(colored, img, leaf_mask)

d = ImageDraw.Draw(img)

# ---- 主叶脉（微弯，白色半透明）----
vein = bezier((56, 128), (118, 121), (172, 135), (224, 128), 60)
d.line(vein, fill=(255, 255, 255, 175), width=6, joint="curve")

# ---- 侧脉：左右各 3 对，从主脉分叉 ----
for k in (1, 2, 3):
    t = k / 4.0
    bx = 56 + (224 - 56) * t
    up = (bx - 22, 128 - 34 - (k - 1) * 5)
    dn = (bx - 22, 128 + 34 + (k - 1) * 5)
    d.line([(bx, 128), up], fill=(255, 255, 255, 110), width=4, joint="curve")
    d.line([(bx, 128), dn], fill=(255, 255, 255, 110), width=4, joint="curve")

# ---- 高光（左上叶面）----
d.ellipse([80, 68, 140, 104], fill=(255, 255, 255, 60))

# ---- 深绿轮廓描边 ----
d.line(leaf_pts, fill=(46, 90, 40, 255), width=5, joint="curve")

# ---- 叶柄 ----
d.rounded_rectangle([16, 122, 44, 134], radius=6, fill=(56, 108, 52, 255))

# ---- 输出 ----
img.save(r"D:\Software\AutoModeASUS\leaf.ico",
         sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
img.save(r"D:\Software\AutoModeASUS\leaf_preview.png")
print("OK: leaf.ico + leaf_preview.png 已生成")
