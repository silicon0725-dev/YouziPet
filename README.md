# YuzuPet 柚松屋桌宠（Godot 4.7 C# 移植版）

> ## 原创声明
> 本桌宠的角色形象、美术素材、部件数据与玩法设计均来自 B 站 **hsbsjsjyshshs** 的原创作品「柚松屋」
> （[B 站个人空间 https://b23.tv/XFLXMQu](https://b23.tv/XFLXMQu)）。
> 本项目是**在获得原作者授权后**，将其安卓应用移植到 Godot / Windows 桌面的移植版，
> 仅供学习交流，**所有原创内容版权归原作者所有**。未经原作者许可，请勿二次分发或商用。

把安卓应用「柚松屋」（青图工作室）的桌宠 + 捏脸换装体验移植到 Windows 桌面。
角色美术与部件数据全部来自原作 APK 解包（作者授权），445 个部件 / 477 层贴图完整复用。

## 功能

- **透明置顶桌宠窗口**：角色空白处点击穿透到桌面（按剪影多边形动态计算）
- **捏脸换装**：29 个分类（前发/后发/马尾/眼睛/瞳孔/嘴巴/上衣/裙子/兽耳/尾巴/翅膀…）
  445 个部件 + 9 色染色板 + 6 档肤色 + 4 套预设穿搭
- **存档兼容原作**：`user://appearance.json` 结构与原作 `cotton-closet-v22` 一致
- **互动**：拖拽（抓身体）、摸头（点头部，有反应台词）、投喂（好感度 +3）、
  跳舞（21 种动作表，当前实现 5 类程序动画）、待机呼吸 + 随机眨眼 + 闲聊气泡
- **系统托盘**（godot-native-tray）：托盘菜单（捏脸/投喂/跳舞/站好/退出），
  左键单击托盘切换桌宠显示隐藏
- **任务栏隐身**（godot_dwm）：WS_EX_TOOLWINDOW，桌宠不占任务栏、Alt-Tab 不可见
- **2D 面捕**（v2 ✅）：控制条"捕"按钮或托盘菜单开启。**OpenSeeFace**（BSD-2，
  VSeeFace/VTube Studio 的引擎，`tools/OpenSeeFace/`，含 numpy2 兼容补丁）经
  `run_tracker.bat` 启动 → 二进制 UDP → `FaceCaptureOpenSee` 解析 → FaceRig 按原作
  live 模式映射（110ms 平滑/190ms 身体跟随/头发延迟、歪头 roll、俯仰带动手臂、
  眨眼/笑眼/张嘴滞回阈值）。solvePnP 头部姿态 + 分眼眨眼 + 嘴角微笑 + 眉毛特征。
  旧 mediapipe sidecar 保留为演示流（`YUZUPET_FACECAP=demo`）；
  `YUZUPET_PYTHON` 可指定解释器路径

## 运行

**直接玩**：双击 `dist/YuzuPet.exe`（独立 exe，无需打开 Godot；面捕需 python，见下）。

用 Godot Hub 打开本目录（引擎 4.7.2-stable mono），F5 运行；或命令行：

```bash
GODOT="<Godot_v4.7.2-stable_mono_win64.exe 路径>"
dotnet build
"$GODOT" --path .
```

自检模式（截图后自动退出，用于验证渲染）：

```bash
YUZUPET_SELCHECK=1 "$GODOT" --path .        # 截主窗口 -> user://selfcheck.png
YUZUPET_SELCHECK=1 YUZUPET_WARDROBE=1 "$GODOT" --path .   # 连捏脸窗口一起截
```

导出 exe：`Godot_v4.7.2-stable_mono_win64.exe --headless --export-release "Windows Desktop"`
（依赖 YuzuPet.sln；导出后确认 dist/ 旁有 tools/ 目录——sidecar 脚本不打包进 exe）。

## 面捕依赖（OpenSeeFace）获取

OpenSeeFace（BSD-2，emilianavt）不随仓库分发，首次使用面捕前手动获取：

```bash
cd tools
git clone --depth 1 https://github.com/emilianavt/OpenSeeFace.git
cd OpenSeeFace
# 应用本仓库的 numpy2/opencv5 兼容补丁（solvePnP 返回形状变化）
git apply --ignore-whitespace ../openseeface-numpy2.patch
# 依赖（或 uv sync）
pip install onnxruntime opencv-python
# 验证：列出摄像头
python facetracker.py --list-cameras 1
```

运行时 Godot 通过 `tools/OpenSeeFace/run_tracker.bat` 拉起 tracker（该 bat 在首次设置后
按 README 中"面捕"一节的内容创建即可；也可参考 dist 构建说明）。

## 结构

```
src/Main.cs                 入口：窗口/交互/控制条/气泡/穿透
src/Pet/PetActor.cs         分层 sprite 渲染器（PSD 坐标直出 + 染色 + 眨眼 + 动作）
src/Pet/PetBrain.cs         状态机/好感度/台词
src/Character/PartCatalog.cs  部件目录（data/character_catalog.json）
src/Character/Appearance.cs   穿戴数据 + 存档
src/UI/WardrobeWindow.cs    捏脸换装编辑器
src/Capture/IFaceCapture.cs 面捕接口（v2 预留）
data/character_catalog.json 从原作导出的部件/配色/预设/动作表
assets/parts/*.png          477 张部件贴图（原作 PSD 分层导出）
```

## 致谢

- 角色美术、部件与文案：青图工作室「柚松屋」（作者授权移植）
- 原作第三方素材：ZCOOL KuaiLe 字体（OFL 1.1）、Kenney 音效（CC0）、Google MediaPipe

## 路线图

- [x] ~~v1.1：托盘图标~~（godot-native-tray，已完成）
- [x] ~~v1.2：骨骼动画 + 21 种舞步 + layerRules~~（已完成）
- [x] ~~v2：2D 面捕~~（Python MediaPipe sidecar + FaceRig，已完成）
- [x] ~~v2.1：拖拽摆锤物理~~（CottonLiftPhysics 1:1 移植：七关节/抓起踢腿冲量/120Hz 子步，
  提起来甩动手脚躯干随惯性摆动）+ 面捕半闭眼三档
- [x] ~~glint 眼部高光 + 分侧眼睑~~（原作 eyeLayer/highlightJelly 1:1：AtlasTexture 左右半幅、
  眼睑压扁 scaleY=0.03+0.97·openness、眼球随头位平移、高光果冻弹簧 k=180/d=16 + 睁眼回弹）
- [ ] v2.2：meme 表情贴图、多宠同屏
