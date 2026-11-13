using LongStitch.App.UI;
using LongStitch.App.Imaging;
using LongStitch.Core;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;
using LongStitch.Core.Preview;

namespace LongStitch.Checks;

/// <summary>
/// 第十四轮需求三：调试读数层（F8）自己不许成为卡顿的来源。
///
/// ═══ 这一组格子守的是哪几句话 ═══
/// 需求三把要求写得很具体，逐条都要有读数能对得上：
/// <list type="number">
///   <item><b>默认关闭</b> → <c>canvas/读数层默认关着：只属于读数层的那笔开销必须是零</c>。
///         （这一格是审计时补的：以前只把"默认 false"写在散文里、推给同组那格
///         <c>align/关掉调试层不改对齐、蓝线、导出字节与命中区</c>，
///         而那格自己把开关设过 true/false，量不到"新建出来就是关的"——把默认值改成 true 它照样全绿）；</item>
///   <item><b>只有那五个量变了才重绘</b> → 这句话的两个半边各有一格：
///         "不许多余重绘" = <c>canvas/读数层的闸门：静止二十帧只重建一次面板、几何一次都不重算</c>；
///         "这些变了必须重绘（漏一个 = 屏幕上留着上一帧的位置，那是错不是慢）" =
///         <c>canvas/需求三那五个触发量各自都得让叠层缓存失效</c>；</item>
///   <item><b>颜色区域 / 文字面板 / 设备矩形用缓存</b> → 实现口径要写清，因为它与需求原文有一处刻意的偏差：
///         <list type="bullet">
///           <item><b>换算</b>缓存：Rect1/Rect2/带/切点/三块命中区/箭头全部算好一次存着（<c>HudGeometryRebuilds</c> 计数），
///                 静止连画 20 帧一次都不重算，视口一动就必须重算（正反两头都量，否则"0 次"可能只是"什么都没画"）；</item>
///           <item><b>文字面板</b>才是缓存位图（10Hz 闸门，其余帧直接贴那张位图，<c>HudPanelRebuilds</c> 计数）；</item>
///           <item>色块本身仍每帧按<b>缓存好的矩形</b>填充，没有再包一层位图——重叠带在拖动期间每帧都动，
///                 把它做成位图等于每帧重建位图，比直接 FillRectangle 更贵。所以这一条按需求<b>意图</b>
///                 （不要每帧重新换算、不要每帧重建文本）落实，而不是按"所有调试内容都必须是位图"的字面落实。</item>
///         </list></item>
///   <item><b>数字刷新限到 5–10Hz</b> → 同上一项的 <c>HudPanelRebuilds</c>：一个 100ms 窗口里最多重建一次；</item>
///   <item><b>每帧不许新建 Brush / Font / 排 11 行文字 / 重建整块位图</b> →
///         量"开着读数层比关着每帧多分配多少字节"：稳态应当接近 0；</item>
///   <item><b>不许调 RenderWhole / 完整 CanvasLayout.Build / 全量清缓存 / 全量解码</b> →
///         量那四个计数器在 20 帧里的增量（新增的 <c>CanvasLayout.BuildCountForTest</c> 就是为了这句话能被量）。</item>
/// </list>
///
/// ═══ 为什么这些是"计数"而不是"计时" ═══
/// 帧耗时那一批由 <c>--gui</c> 在真窗口里量（那是需求五的活），电池这边刻意<b>不</b>拿毫秒当判据——
/// 离线毫秒数受机器负载影响太大（第十三轮就出现过同一份代码 17ms 与 84ms 并存），
/// 拿它当常驻断言只会得到"今天绿明天红"的噪声。能被判死的只有"做没做这件事"这种计数。
/// </summary>
internal static class HudCostTests
{
    private const int Width = 60;
    private const int Height = 600;
    private const int Overlap = 120;

    public static void Register()
    {
        Battery.Add("canvas", "开着读数层连画二十帧：不重算布局、不再清缓存、不再渲染瓦片", () =>
            UiSmokeTests.RunOnStaThread(HudPaintNeverTouchesTheDataLayer));
        Battery.Add("canvas", "读数层的闸门：静止二十帧只重建一次面板、几何一次都不重算", () =>
            UiSmokeTests.RunOnStaThread(IdleFramesRebuildOnlyOnce));
        Battery.Add("canvas", "读数层每帧的额外堆分配：不新建 Brush、Font，也不重排文本", () =>
            UiSmokeTests.RunOnStaThread(HudAddsAlmostNoAllocation));
        Battery.Add("canvas", "读数层默认关着：只属于读数层的那笔开销必须是零", () =>
            UiSmokeTests.RunOnStaThread(HudIsOffByDefaultAndCostsNothing));
        Battery.Add("canvas", "需求三那五个触发量各自都得让叠层缓存失效", () =>
            UiSmokeTests.RunOnStaThread(EachNamedTriggerInvalidatesOverlay));
        Battery.Add("canvas", "面板位图每帧只走一次不缩放贴图 贴几帧就计几", () =>
            UiSmokeTests.RunOnStaThread(PanelBlitsAreUnscaledOnePerFrame));
        Battery.Add("canvas", "面板重画复用同一张缓冲区 重建多次但分配只涨必要那几次", () =>
            UiSmokeTests.RunOnStaThread(PanelRebuildReusesTheBuffer));
        Battery.Add("guard", "面板贴图计数分得清不缩放与重采样两种贴法", () =>
            UiSmokeTests.RunOnStaThread(BlitCountingDistinguishesForms));
        Battery.Add("canvas", "F8 面板的背景框必须装得下它自己那些读数", () =>
            UiSmokeTests.RunOnStaThread(PanelBoxContainsItsOwnReadouts));
        Battery.Add("guard", "面板行高若退回写死的 16 像素则在高分屏下切掉最后几行", () =>
            UiSmokeTests.RunOnStaThread(FixedLineHeightWouldClipOnHiDpi));
        Battery.Add("guard", "面板宽度若退回按字符数估则含全角字的行一定被截断", () =>
            UiSmokeTests.RunOnStaThread(CharCountWidthWouldClipCjkLines));
        // 第三十六改：真窗口新增那道「面板装得下自己的读数（真 DPI）」的门，共用这里这一个对照函数——
        // 门要能红才是门，所以钉它抓得住只差一像素的窄框（正向对照在同一格里）。
        Battery.Add("guard", "真窗口那道面板闸门要能抓住算窄的框", () =>
            UiSmokeTests.RunOnStaThread(RealWindowPanelGateCatchesOnePixelNarrow));
        Battery.Add("canvas", "画布角标的框必须装得下它那行字", () =>
            UiSmokeTests.RunOnStaThread(BadgeBoxContainsItsOwnText));
        Battery.Add("guard", "角标的框故意做窄则墨一定变少", () =>
            UiSmokeTests.RunOnStaThread(NarrowBadgeBoxMustLoseInk));
        Battery.Add("guard", "面板分配计数分得清复用与每次重建都分配", () =>
            UiSmokeTests.RunOnStaThread(AllocationCountingDistinguishesPolicies));
        // ═══ 第二十一改：行级那道算术闸改由产品自己交出一句话 ═══
        // 面板走行级还是走整块，判的是一道整数算术（行距×行数＋6 == 面板高），而<b>行距按进程 DPI 现量</b>。
        // 过去这句只有 `--gui` 工装算得出来，那个进程没声明 DPI 感知 ⇒ 它算的永远是 96 DPI 那一档，
        // 用户那台 150% 屏上这道闸成不成立<b>没有任何出口能读</b>（屏上没有、复制诊断信息里没有、导出会话里也没有）。
        // 现在这句话进 DescribeDiagnostics，三个出口同源；工装改成复用这一句，同一道算术不许两份写法。
        // 判据两头判（文本里的数＝属性读数、文本的结论＝产品真走的那条路），反证那一格钉住它不是写出来的常数。
        Battery.Add("canvas", "诊断文本要带出行级那道算术闸的两头读数", () =>
            UiSmokeTests.RunOnStaThread(DiagnosticTextCarriesTheLineWiseGate));
        Battery.Add("guard", "算术闸那句话画第一帧之前必须说不成立", () =>
            UiSmokeTests.RunOnStaThread(GateLineWouldLieBeforeTheFirstPaint));
        // 第三十七改：150% 那一屏「面板装不装得下」工装量不到（宿主进程 96 DPI），
        // 而截图会拍到聊天内容（隐私红线）——所以把这两个数与判词交给产品自己的诊断出口，
        // 用户一次「复制诊断信息」就能交回来，不必判断也没让隐私内容离开这台机器。两头读数＝属性读数。
        Battery.Add("canvas", "诊断文本要交出面板装不装得下那两个数与判词", () =>
            UiSmokeTests.RunOnStaThread(DiagnosticTextCarriesPanelFitLine));
        Battery.Add("guard", "面板自证那句在画第一帧之前必须说量不到", () =>
            UiSmokeTests.RunOnStaThread(PanelFitLineWouldLieBeforeTheFirstPaint));
        Battery.Add("canvas", "超出屏幕那几笔描边必须先裁到可见区 裁过才算数", () =>
            UiSmokeTests.RunOnStaThread(OutlineIsClippedToTheVisibleArea));
        Battery.Add("guard", "描边若不裁到可见区则虚线路径代价翻红",
            UnclippedOutlineWouldCostTheWholePath);
        Battery.Add("guard", "读数层若退回每帧排文本与重建位图则堆分配立刻翻红",
            OldPerFrameHudWouldBlowTheBudget);
        // 需求五那条 `单帧 ≤ 50ms` 判的必须是"拖动时的帧"：真窗口 150% 量下来全池最慢那一枚
        // 是导入/首次绘制帧（池 4096 格，一趟拖动转不出去），所以产品自己记住手势起点。
        Battery.Add("canvas", "拖动段的帧读数只算最近一次按下之后的帧", () =>
            UiSmokeTests.RunOnStaThread(DragSegmentCountsOnlyFramesAfterPress));
        Battery.Add("guard", "拖动段若退回整个样本池则导入帧会冒充拖动最慢帧", () =>
            UiSmokeTests.RunOnStaThread(WholePoolWouldImpersonateTheDragSegment));
        // 需求六"卡顿发生在哪个阶段"：分阶段读数必须能加回这一帧的总时长（详见下面两格的注释）
        Battery.Add("canvas", "最慢帧分阶段要把整帧的账加满", () =>
            UiSmokeTests.RunOnStaThread(StageBreakdownAccountsForTheWholeFrame));
        Battery.Add("guard", "分阶段只报前三段时这一帧的账加不满", () =>
            UiSmokeTests.RunOnStaThread(ThreeSegmentsAloneWouldUnderAccount));
        // 第五改：叠层那一笔往下再拆六列（详见下面两格的注释）——拆账必须加得回叠层，还得跟着开关动。
        Battery.Add("canvas", "叠层那一笔要能再拆到底", () =>
            UiSmokeTests.RunOnStaThread(OverlaySplitAccountsWithTheOverlayBucket));
        Battery.Add("guard", "拆账必须跟着开关动", () =>
            UiSmokeTests.RunOnStaThread(OverlaySplitTracksWhichLayersWereDrawn));
        // 第二十五改：预览那一列（叠层里最大那一笔，真窗口逐趟最慢帧读到 10.1~14.7ms）往下再拆三段。
        Battery.Add("canvas", "预览那一笔要拆成渲染、拼对照与比并贴三段", () =>
            UiSmokeTests.RunOnStaThread(PreviewSplitsIntoRenderStitchBlit));
        Battery.Add("guard", "比并贴那一段的位图转换次数只许跟着真转换涨", () =>
            UiSmokeTests.RunOnStaThread(PreviewConversionCountTracksTheRealConversion));
        // 第二十六改：拼对照那一段先交出行数与字节数（上一改只交出一笔毫秒，指不出该动哪一种钱）。
        Battery.Add("canvas", "拼对照那一步要交出自己真取了几行", () =>
            UiSmokeTests.RunOnStaThread(DiscardedCopyReportsItsOwnRowsAndBytes));
        Battery.Add("guard", "拼对照的行数与字节数只许跟着真取数涨", () =>
            UiSmokeTests.RunOnStaThread(DiscardedCountsIgnoreFramesWithoutTheStitch));
        // 第二十七改：色块层那三笔大矩形此前是照缓存里的整块设备矩形直接下笔（拖对齐时能有几万行高）。
        Battery.Add("canvas", "色块层那三笔大矩形也要先裁到可见区", () =>
            UiSmokeTests.RunOnStaThread(ColorLayerDrawsOnlyTheVisiblePart));
        Battery.Add("guard", "带整条在屏外时色块层那一组一笔都不许下", () =>
            UiSmokeTests.RunOnStaThread(ColorLayerDrawsNothingWhenBandIsOffScreen));
        // 第三十改：需求三第 3 条剩下的最后一项——颜色区域的<b>像素</b>要不要也走缓存位图，先量不猜。
        Battery.Add("canvas", "色块层换成缓存位图会改屏上像素", () =>
            UiSmokeTests.RunOnStaThread(ColorLayerCachedBitmapChangesPixels));
        Battery.Add("guard", "色块层两条路的像素对照不许由两边都没画喂绿", () =>
            UiSmokeTests.RunOnStaThread(ColorLayerEquivalenceRequiresRealStrokes));
        // 第三十一改：贴图循环里那笔"铺完再盖住"的纯搬运（＋每铺一次新建两支刷子）。
        Battery.Add("canvas", "棋盘格只铺在不透明判定为假的瓦片上", () =>
            UiSmokeTests.RunOnStaThread(CheckerOnlyDrawsWhereTransparencyShows));
        Battery.Add("guard", "棋盘格判定不许把带透明的瓦片判成不透明", () =>
            UiSmokeTests.RunOnStaThread(CheckerSkipMustNotSwallowTransparentTiles));
        // 第三十二改：把需求三第 5 条那半句量到整帧（以前只有读数层那一族有 ≤512B 的门）。
        Battery.Add("canvas", "稳态帧每帧的堆分配要有上限且画布那一族也算", () =>
            UiSmokeTests.RunOnStaThread(WarmCanvasFrameHasBoundedHeapAllocation));
        Battery.Add("guard", "每帧分配读数要看得见一帧里的额外活", () =>
            UiSmokeTests.RunOnStaThread(FrameAllocationReadingSeesExtraWorkPerFrame));
        // 第三十三改（归因，不是降耗）：整帧那一笔 MB 级分配要往下拆到取数／转换／读数层三段。
        // 真窗口读到 6.4~8.3 MB／帧（同命令行每帧重算 6~8 块），电池稳态帧 88 KB／帧——
        // 差 70 倍这件事落在哪一段决定修法（收窄作废范围／复用缓冲／分摊转换），没读数就没资格决定动谁。
        Battery.Add("canvas", "每帧分配要按取数与转换两段各自交出", () =>
            UiSmokeTests.RunOnStaThread(FrameAllocationSplitsIntoFetchAndConvert));
        Battery.Add("guard", "每帧分配的分段读数只许跟着真取数真转换涨", () =>
            UiSmokeTests.RunOnStaThread(AllocStageReadingsFollowRealWorkOnly));
        // 第二十九改：需求三第 4 条点名那六个触发量，各自都要把屏上对应那一行换掉（闸门那一侧早有格钉着）。
        Battery.Add("canvas", "读数层那六个触发量各自都要换掉自己那一行", () =>
            UiSmokeTests.RunOnStaThread(HudSixTriggersEachMoveTheirOwnLine));
        Battery.Add("guard", "某一行的值故意留在旧值时逐行核对必须抓住它", () =>
            UiSmokeTests.RunOnStaThread(PerLineCheckCatchesOneStaleValue));
        // 拆账之后动手改的那一笔：缝状态标签的宽度只量一次（GDI 笔刷提到静态）
        Battery.Add("canvas", "缝状态标签的宽度只量一次", () =>
            UiSmokeTests.RunOnStaThread(SeamLabelWidthIsMeasuredOnce));
        Battery.Add("guard", "标签宽度缓存的键必须含字体与 DPI", () =>
            UiSmokeTests.RunOnStaThread(SeamLabelCacheKeyIncludesFontAndDpi));
        // 第七改：屏外的缝不再白烧把手与状态标签（判据两头各判、反证判"每帧重判"）
        Battery.Add("canvas", "屏外的缝不画把手与状态标签", () =>
            UiSmokeTests.RunOnStaThread(OffScreenSeamsSkipTheirChrome));
        Battery.Add("guard", "屏外早退不许粘住可见的那一条", () =>
            UiSmokeTests.RunOnStaThread(OffScreenSkipIsRecomputedEveryFrame));
        // 第八改：对照层只取屏上看得见的那几行（25% 那档量到过一帧 316.2ms、拆账指着"预览"291.6ms）。
        // 这一改动的是"画出去多少"，所以判据必须是像素而不是耗时——耗时是机器的，错行的对照层照样很快。
        Battery.Add("canvas", "对照层裁到可见区后每行像素不许变", () =>
            UiSmokeTests.RunOnStaThread(CompareLayerClipKeepsEveryRowIdentical));
        Battery.Add("guard", "对照层错一行必须被字节比较抓到", () =>
            UiSmokeTests.RunOnStaThread(CompareLayerRowShiftIsCaughtByBytes));
        // 第九改（归因，不是降耗）：取数那一笔要能报出"其中首次解码花了多少"。
        // 文档里那句"一块瓦片内部的冷取数拆不开（含那张源图首次解码）"挂了四批，
        // 括号那半句一直没量过——本批把它变成读数，两格：正向报得出、反证不许跟着缓存命中涨。
        Battery.Add("canvas", "取数那一笔要能报出首次解码占了多少", () =>
            UiSmokeTests.RunOnStaThread(FetchStageReportsFirstDecodeInside));
        Battery.Add("guard", "解码归因不许跟着缓存命中涨", () =>
            UiSmokeTests.RunOnStaThread(DecodeAttributionFollowsRealDecodesOnly));
        // 第十改：读数层里最大那一块（描边）往下拆两半——"永远要画的边界"与"只有选中缝才画的命中区"。
        // 正判据量两半各有数、加起来不许超过整笔；反证量命中区那半**只跟着真画出去的笔数**涨。
        Battery.Add("canvas", "描边那一笔要拆成边界与命中区两半", () =>
            UiSmokeTests.RunOnStaThread(OutlineSplitsIntoBorderAndHitZones));
        Battery.Add("guard", "命中区那半笔只许跟着真画出的笔数涨", () =>
            UiSmokeTests.RunOnStaThread(HitZoneHalfOnlyMovesWhenHitZonesAreDrawn));
        // 第十一改：贴图那一笔（真窗口读到过 50.5ms）往下拆两半——"这块瓦片第一次拷成 GDI 位图"
        // 与"每帧必做的重采样"。这两半的修法相反（分摊到几帧 / 途中走便宜档），不劈开就没资格决定动谁。
        Battery.Add("canvas", "贴图那一笔要拆成首次转换与重采样两半", () =>
            UiSmokeTests.RunOnStaThread(BlitSplitsIntoConvertAndResample));
        // 第二十四改：第十一改那两半用的是<b>同一对时钟</b>（重采样＝父项减子项），于是"一块 512×512
        // 的转换到底值几毫秒"至今没有读数——下一改要决定的正是转换该不该分摊到几帧。
        Battery.Add("canvas", "贴图那一笔的两半各自要有自己的计时点", () =>
            UiSmokeTests.RunOnStaThread(BlitHalvesHaveTheirOwnClocks));
        Battery.Add("guard", "把整段贴图都算进首次转换那一半会让重采样那半永远是 0", () =>
            UiSmokeTests.RunOnStaThread(OneClockForBothHalvesWouldLeaveResampleEmpty));
        Battery.Add("guard", "转换那半笔只许跟着真转换的块数涨", () =>
            UiSmokeTests.RunOnStaThread(ConvertHalfOnlyMovesWithRealConversions));
        // 第十二改：第十一改把「贴图那一笔」指认到转换之后，动的是转换的实现本身。
        // 动像素的改动只能用字节钉，动分配只能用分配钉，所以两对：逐字节＋反写序、分配形状＋整块例子。
        Battery.Add("canvas", "首次转换出来的 BGRA 必须与独立复算逐字节相同",
            TileConversionMatchesIndependentRecompute);
        Battery.Add("guard", "红蓝换序写反必须被逐字节判据抓到", ConversionSwizzleReversalIsCaught);
        Battery.Add("canvas", "首次转换的分配量必须按行走而不是整块",
            TileConversionAllocatesPerRowNotWholeBlock);
        Battery.Add("guard", "分配量那把尺必须能量出整块写法", AllocationYardCatchesWholeBufferShape);
        // 第十四改（归因，不是降耗）：读数层那一笔要<b>按帧</b>拆。第十三改在真窗口量到
        // "开 HUD 那档最慢一帧 97.9ms 里读数层自己占 33.5ms"，可那 33.5ms 里装着三种修法相反的
        // 钱（面板重建带一次大对象投放／那几笔虚线描边／把面板贴上去），而第十改那批的出口全是
        // <b>进程累计</b>（工装自己作差报"这一趟"），按帧那份账一直没开。两格：本帧拆到底＋量的尺分得清本帧与累计。
        Battery.Add("canvas", "读数层那一笔要按帧拆到底且加起来不许超过整段", () =>
            UiSmokeTests.RunOnStaThread(HudStageSplitsPerFrameInsideTheFrameTotal));
        Battery.Add("guard", "读数层那把帧尺分得清本帧增量与进程累计", () =>
            UiSmokeTests.RunOnStaThread(FrameYardDiffersFromProcessCumulativeCounters));
        // 第十五改（降耗）：第十四改把最慢那帧的钱指认到"面板重画那一路的排字"（2.7~3.1ms／一帧里通常只有
        // 1~3 行的数字变了，付的却是整段十好几行的钱）。这一改把复用那条路改成<b>从第一条变化行往下重排</b>——
        // 省下的像素必须一个都不许多：正判据比的是<b>同一句文字</b>走行级与走整块两张缓冲区的字节，
        // 反证故意把行级落位错开 1 像素，证明那把尺真看得见一行的高低。
        Battery.Add("canvas", "面板走行级增量重画必须与整块重画逐字节相同", () =>
            UiSmokeTests.RunOnStaThread(LineWisePanelRepaintMatchesWholePanelRepaint));
        Battery.Add("guard", "行级重画错开一行必须被逐字节判据抓到", () =>
            UiSmokeTests.RunOnStaThread(LineWiseRepaintYardCatchesOneRowShift));
        // 同一改的第二组（接线，不是降耗）：新路绕开了 PaintHudPanelInto，第十四改那两笔账差点成了白账——
        // 真窗口读到"复用 7.9ms 而清底／排字都报 0"才发现。两格一头一尾：新路自己交得出账，尺也还得管得住旧路。
        Battery.Add("canvas", "行级那条重画路必须自己交得出清底与排字两笔", () =>
            UiSmokeTests.RunOnStaThread(LineWiseRoadChargesItsOwnTwoSubItems));
        Battery.Add("guard", "两笔面板计时只许跟着真重画涨整块那条路也一样", () =>
            UiSmokeTests.RunOnStaThread(WholePanelRoadChargesTheSameTwoSubItems));
    }

    /// <summary>
    /// 给两格共用的"文件源"底料：往临时目录写一张真 PNG（走<b>产品自己的</b>编码器），
    /// 再拿它注册一个 <see cref="FileSourceResolver"/>。
    /// 为什么必须走文件而不是内存源：首次解码这一步只在文件通路上存在，
    /// 用内存源量的那一格只能证"没解码时报 0"，证不了"解码时报得出来"。
    /// </summary>
    private static (string Dir, FileSourceResolver Resolver, string SourceId) NewDiskResolverWithOnePng()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        RgbaImage img = LongScrollTests.StripedPage(64, 400);
        string file = Path.Combine(dir, "a.png");
        File.WriteAllBytes(file, LongStitch.Core.Imaging.Png.PngEncoder.EncodeToBytes(img));
        var resolver = new FileSourceResolver();
        resolver.Register(new SourceImage
        {
            Id = "src_decode_probe", Path = file, FileName = "a.png",
            Size = new IntSize(64, 400), Format = "png",
        });
        return (dir, resolver, "src_decode_probe");
    }


    // ───────────────────────── ⓪ 默认关闭（需求三第 1 条） ─────────────────────────

    /// <summary>
    /// 需求三第 1 条只有六个字："HUD 默认关闭"。这一格之前<b>没有</b>直接判据——
    /// 文件开头那句话把它推给 <c>align/关掉调试层不改对齐、蓝线、导出字节与命中区</c>，
    /// 但那格是自己把开关设成 true 再设回 false 量的"关掉之后不改几何"，
    /// <b>量不到"新建出来那一刻它就是关的"</b>（把默认值改成 true，那格照样全绿）。
    /// 这属于本项目反复犯的那一族：散文写着有账、实际没量。
    ///
    /// 所以这里两头都量：
    /// · 关着（默认）画一帧，<b>只属于读数层的那两笔开销必须是 0</b>——文字面板一次都不排
    ///   （<c>HudPanelRebuilds</c> 只在读数层里涨）、分档读数的样本一帧都不记
    ///   （<c>HudSplitStats.Frames</c> 只在开着时涨）；
    /// · 打开同一台画布再画一帧，面板计数与分档样本必须动起来（正向对照，
    ///   否则上面那两个 0 可能只是"计数器根本没接上"）。
    ///
    /// ═══ 本格第一次跑就红了一次，但红的是判据不是实现 ═══
    /// 我原本写的是"关着画一帧 ⇒ 叠层几何换算也必须 0 次"。跑出来是 1 次，查了调用点才发现
    /// 那份缓存<b>不只服务读数层</b>：重影对比层（<c>ShowCompareOverlay</c> 是产品功能、默认就在画）
    /// 与「复制诊断信息」都用它。所以那一笔换算是重影层的成本，不该记在读数层头上——
    /// 需求三第 5 条禁的是"每帧重新换算"，不是"关读数层时一次都不换算"。
    /// 判据据此改成量"≤ 1 次"（共享缓存确实生效），并把这个来源写在这里，
    /// 免得下一轮有人把它又改回 0 或者干脆删掉。
    /// </summary>
    private static void HudIsOffByDefaultAndCostsNothing()
    {
        using Harness h = Harness.New();
        Battery.Assert(!h.Canvas.ShowDebugHud,
            "新建的画布控件 ShowDebugHud 必须是 false（需求三第 1 条：默认关闭）——" +
            "这一条要是不成立，用户每次打开程序都在为一份看不见的读数层付帧时");

        h.Render();                                       // 一帧：走完整 OnPaint 通路，但不开读数层
        Battery.AssertEqual(0L, h.Canvas.HudPanelRebuilds,
            "默认关着画一帧：文字面板一次都不许排（这个计数只在读数层里涨，是读数层自己的账）");
        Battery.AssertEqual(0, h.Canvas.HudSplitStats.Frames,
            "默认关着画一帧：不该有任何一帧被记成开着读数层（分档读数的分母必须是真开过的帧）");
        Battery.Assert(h.Canvas.HudGeometryRebuilds <= 1,
            $"默认关着画一帧：叠层几何换算 {h.Canvas.HudGeometryRebuilds} 次，超过 1 次就是" +
            "「每帧都重新换算」——注意这一笔本来就要花一次（重影对比层共用那份缓存），" +
            "所以判的是 ≤1 不是 =0");

        h.Canvas.ShowDebugHud = true;
        h.Render();
        Battery.Assert(h.Canvas.HudPanelRebuilds >= 1 && h.Canvas.HudSplitStats.Frames >= 1,
            $"正向对照：打开之后画一帧，面板重建 {h.Canvas.HudPanelRebuilds} 次、" +
            "分档样本都必须动起来——上面那两个 0 因此不是「计数器没接上」");
        Battery.Evidence($"默认关着：面板 0 次、分档样本 0 帧、几何换算 {h.Canvas.HudGeometryRebuilds} 次" +
                         $"（≤1，重影层共用）；打开一帧后面板 {h.Canvas.HudPanelRebuilds} 次、" +
                         $"几何 {h.Canvas.HudGeometryRebuilds} 次、分档样本 {h.Canvas.HudSplitStats.Frames} 帧");
    }

    // ─────────────────── ⓪a1 面板贴图必须 1:1，不许走缩放管线 ───────────────────

    /// <summary>
    /// 这一格钉的是需求三第 3 条里"文字面板用缓存位图"那半句的<b>另一半</b>：
    /// 缓存了位图，还得<b>不缩放</b>地贴上去。
    ///
    /// 为什么要专门量这个：面板尺寸与贴图目标尺寸本来就相等（1:1），
    /// 可代码里当时调的是 <c>DrawImage(bmp, x, y, w, h)</c>——那一族重载一律走缩放管线，
    /// 而画布的 InterpolationMode 拖动中是 Bilinear、静止是 HighQualityBilinear，
    /// 于是每帧把这张约 600×250 的面板重采样一遍，采出来的结果与源像素一样，
    /// 那几毫秒纯属白花。<b>屏幕像素看不出区别</b>（两种贴法结果相同），
    /// 所以这条只能靠控件里的计数钉住：<see cref="StitchCanvasControl.HudPanelUnscaledBlits"/>
    /// 只在不缩放贴图那一行涨。
    ///
    /// 两头都量：开着读数层连画四帧必须正好四次；关掉读数层再画三帧一次都不许涨
    /// （否则这个计数可能是"每帧自动涨"的假账）。贴法的辨别交给
    /// <c>guard/面板贴图计数分得清不缩放与重采样两种贴法</c>。
    /// </summary>
    private static void PanelBlitsAreUnscaledOnePerFrame()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();                                     // 暖机帧：面板在这一步才建起来
        long before = h.Canvas.HudPanelUnscaledBlits;

        for (int i = 0; i < 4; i++) h.Render();
        long growth = h.Canvas.HudPanelUnscaledBlits - before;
        Battery.AssertEqual(4L, growth,
            $"开着读数层连画 4 帧，不缩放贴图必须正好 4 次（实际 {growth} 次）。" +
            "少了就是有人把贴图改回了带尺寸的 DrawImage——那一帧的钱会重新花在重采样上，" +
            "而屏幕上看不出任何区别");

        h.Canvas.ShowDebugHud = false;
        long offBase = h.Canvas.HudPanelUnscaledBlits;
        for (int i = 0; i < 3; i++) h.Render();
        Battery.AssertEqual(offBase, h.Canvas.HudPanelUnscaledBlits,
            "关掉读数层再画 3 帧：贴图计数一次都不许涨——它要是照涨，上面那个 4 就是在量帧数而不是量贴法");

        Battery.Evidence($"开着 4 帧 ⇒ 不缩放贴图 +4；关着 3 帧 ⇒ +0；" +
                         $"面板重建 {h.Canvas.HudPanelRebuilds} 次（10Hz 闸门，与贴图次数本来就是两个数）");
    }

    /// <summary>
    /// 反证：<see cref="StitchCanvasControl.HudPanelUnscaledBlits"/> 这个计数<b>分得清两种贴法</b>。
    /// 控件上开了一个只走旧写法的测试出口（<c>BlitHudPanelResampledForTest</c>，产品通路一步都不走它），
    /// 用它贴三次，计数必须一次都不涨。
    /// 少了这一格，"计数不涨"就可能是因为它压根不接贴图这条线——那种判据永远绿，比没有更危险。
    /// </summary>
    private static void BlitCountingDistinguishesForms()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();
        long afterFrame = h.Canvas.HudPanelUnscaledBlits;
        Battery.Assert(afterFrame >= 1,
            $"正向对照：开着读数层画一帧，不缩放贴图至少要有 1 次，实际 {afterFrame} 次");

        using var bmp = new System.Drawing.Bitmap(64, 64);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        for (int i = 0; i < 3; i++) h.Canvas.BlitHudPanelResampledForTest(g);

        Battery.AssertEqual(afterFrame, h.Canvas.HudPanelUnscaledBlits,
            "按旧写法（带目标尺寸的 DrawImage）把同一张面板贴三次，不缩放计数必须一次都不涨。" +
            "它要是也涨，canvas 那一格量的就只是帧数，实现退回旧写法也不会红");
        Battery.Evidence($"旧写法贴 3 次 ⇒ 计数不变（仍 {h.Canvas.HudPanelUnscaledBlits} 次）；" +
                         "计数只在 DrawImageUnscaled 那一行涨");
    }

    // ─────────────── ⓪d 面板框必须装得下它自己的读数（真窗口那十二步跑出来的缺陷） ───────────────

    /// <summary>
    /// 需求四那十二步在真窗口里跑出来的第一个缺陷：<b>F8 面板只画得出前 10 行，
    /// 后 4 行（帧耗时 / HUD 单独耗时 / 分阶段 / Paint 计数）整行不见了</b>。
    /// 病因：面板长宽按"行数 × 写死的 16 像素"算，而位图里的字是按<b>进程实际 DPI</b> 排的
    /// （进程按系统主屏 DPI 渲染——Program.cs 第五十五改⑥起 SystemAware，150% 的主屏上每行 24 像素）——框比字小，出界那几行被 GDI 裁掉。
    ///
    /// ═══ 这一格量的是"装得下"，不是"看着顺眼" ═══
    /// 拿面板<b>此刻那段文字</b>在<b>面板自己那个 DPI</b> 下量出真实占位，再和位图长宽比：
    /// 高、宽两头都必须够。宽度那头也一起钉，因为它是同一个估算法的另一半
    /// （按"字符数 × 等宽单字宽"估，而中日韩全角字一个占两格）。
    ///
    /// ═══ 为什么两种读数形状都要过（本轮补的盲点）═══
    /// 这格第一版只核"选中一条缝"那一档，于是<b>宽度那头当场被假象满足</b>：
    /// 那一档旧写法的框只差几个像素（608 vs 真实 581），被 32 像素分档向上取整抹平
    /// ⇒ 190 格全绿，而真窗口上"没选中拼缝"那一档
    /// 的「分阶段 …（其中重新分配缓冲区 1 次）」被截到只剩前半句。
    /// 现在两档各过一遍（<see cref="CheckPanelFitsItsReadouts"/>），
    /// 反证配两格：<c>guard/面板行高若退回写死的 16 像素则在高分屏下切掉最后几行</c> 与
    /// <c>guard/面板宽度若退回按字符数估则含全角字的行一定被截断</c>。
    ///
    /// ═══ 为什么必须再配反证 ═══
    /// 电池进程没声明 DPI 感知，它跑在 96 DPI：那里量出来的行距恰好就是 16，
    /// 所以<b>改造前的写法在这一格里同样是绿的</b>——这一格单独存在会被假象满足。
    /// 反证先把"96 DPI 分不出来"这件事本身量出来，再把同一段文字放到能分出来的 DPI 下量，
    /// 缺陷才真的落在判据上。
    /// </summary>
    private static void PanelBoxContainsItsOwnReadouts()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        // 两种形状都要过：只核"选中一条缝"那一档，判据会被那档的假象满足——
        // 那一档旧写法估出来的框只差几个像素（当场量到 608 vs 真实 581），被 32 像素的分档向上取整抹平；
        // 真窗口上被截的是"没选中拼缝"那一档（395→416 vs 真实 546），见 guard 那格。
        string seamShape = CheckPanelFitsItsReadouts(h, "选中一条缝");
        h.Canvas.SelectSeam(null);
        string idleShape = CheckPanelFitsItsReadouts(h, "没选中拼缝");
        // 两档各归各的自证：面板重建有 10Hz 闸门，第二档要是没等过闸门就读，拿回来的是上一档的缓存文本，
        // 这一格就又变成"只量一档、量两遍"——本轮第一次就是这么静悄悄过去的。
        Battery.Assert(!seamShape.Contains("没选中拼缝") && idleShape.Contains("没选中拼缝"),
            "夹具自证：两档必须真是两种读数形状（选中档不含「没选中拼缝」那句、没选中档含）。" +
            "两串一样就说明 10Hz 闸门把第二档挡在了缓存里，那些数成了同一档量两遍，另一档的缺陷依旧量不到");
        Battery.Assert(idleShape.Split('\n').Length < seamShape.Split('\n').Length,
            $"夹具自证：没选中拼缝那一档的行数本该更少（{idleShape.Split('\n').Length} vs " +
            $"{seamShape.Split('\n').Length}）——不更少同样是第二档没换过来");
    }

    /// <summary>
    /// 反证（精度红线 §3）。第三十六改在<b>真窗口工装</b>那一档新加一道门
    /// 「面板装得下自己的读数（真项目那 14 行）」——它比的是真 17 张图那一屏的读数文本
    /// （含第三十四改加长的四段字节），夹具那两格比不到这份文本。
    /// <b>口径写清：工装进程与电池进程一样都是 96 DPI</b>（Checks 没声明 DPI 感知，现量 进程 DPI 96），
    /// 所以 150% 那一屏<b>不由这一格负责</b>——那一档的证据在产品自己窗口（进程 DPI 144）跑的那次十二步（#76/#78/#109）。
    /// 门只有"能红"才算门，所以这里钉它读的<b>同一个对照函数</b>对边界真的敏感：
    /// 恰好等于 → 装得下；宽差一像素 → 装不下；高差一像素 → 装不下。
    /// 正向对照（产品自己这一帧本来就装得下）在格内一起判，免得拿一个恒假的对照充数。
    /// </summary>
    private static void RealWindowPanelGateCatchesOnePixelNarrow()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();
        (int boxW, int boxH) = h.Canvas.HudPanelSizeForTest;
        Size need = StitchCanvasControl.HudTextExtent(h.Canvas.HudTextForTest);
        Battery.Assert(GuiPerfHarness.PanelFitsReadouts(new Size(boxW, boxH), need),
            $"正向对照先立不住：产品自己这一帧的框 {boxW}×{boxH} 已经装不下 {need.Width}×{need.Height}——" +
            "真窗口那道门此刻本来就是红的，这里再判窄也说明不了对照函数有效");
        Battery.Assert(GuiPerfHarness.PanelFitsReadouts(need, need),
            "框恰好等于文字真实占位时判成装不下：那道门的边界写反了（≥ 写成了 >），" +
            "于是它会在<b>刚好够用</b>的那一屏误红——误红的门下一轮就被当噪声绕过去了");
        Battery.Assert(!GuiPerfHarness.PanelFitsReadouts(new Size(need.Width - 1, need.Height), need),
            "宽差一像素还说装得下：那道门抓不到「最后一列字被裁在框外」这一族缺陷");
        Battery.Assert(!GuiPerfHarness.PanelFitsReadouts(new Size(need.Width, need.Height - 1), need),
            "高差一像素还说装得下：那道门抓不到「最后一行读数被整行裁掉」这一族缺陷" +
            "（#76 修的就是它——14 行只画得出前 10 行）");
        Battery.Evidence($"产品这一帧：框 {boxW}×{boxH}、文字真实 {need.Width}×{need.Height}" +
                         "；边界三判：恰好＝装得下、宽 −1＝装不下、高 −1＝装不下" +
                         "（真窗口那道门读的就是这一个函数。<b>两处都在 96 DPI 的工装进程里</b>，" +
                         "150% 那一屏的证据不在这里——它在产品自己窗口（进程 DPI 144）跑的那次十二步里）");
    }

    /// <summary>
    /// 面板这一格对<b>一种读数形状</b>的核法：走产品那条 OnPaint 通路把面板建出来，
    /// 再拿"这段文字真实占多大"与"面板那块框多大"对，最后数一遍墨。
    /// 交回那段文字，让调用方核对两档确实是两种形状（<see cref="PanelBoxContainsItsOwnReadouts"/>）。
    /// </summary>
    private static string CheckPanelFitsItsReadouts(Harness h, string shape)
    {
        // 先等过面板那 10Hz 闸门：闸门没开就根本不重算文本，第二档会读到第一档的缓存。
        System.Threading.Thread.Sleep(150);
        h.Render();                                       // 走产品那条 OnPaint 通路，面板在这一步建出来

        string text = h.Canvas.HudTextForTest;
        int lines = text.Count(c => c == '\n') + 1;
        Battery.Assert(shape == "没选中拼缝" ? lines >= 7 : lines >= 14,
            $"夹具自证：{shape}时读数本该有 {(shape == "没选中拼缝" ? 7 : 14)} 行以上，实际 {lines} 行" +
            "——行数不对的话下面那句「装得下」是空的");
        if (shape == "没选中拼缝")
            Battery.Assert(text.Contains("分阶段"),
                $"夹具自证：{shape}这一档必须含「分阶段」那一行——真窗口上被截掉的正是它，" +
                "这档要是没这行，这一格就又量不到那个缺陷了");

        Size need = StitchCanvasControl.HudTextExtent(text);
        (int boxW, int boxH) = h.Canvas.HudPanelSizeForTest;
        Battery.Assert(boxH >= need.Height,
            $"{shape}：面板框高 {boxH} 装不下这段 {lines} 行读数的真实高度 {need.Height}（差 {need.Height - boxH} 像素）：" +
            "出界那几行会被 GDI 整行裁掉，需求四点名的「当前帧耗时 / P95 帧耗时」就从屏上消失了");
        Battery.Assert(boxW >= need.Width,
            $"{shape}：面板框宽 {boxW} 装不下最长那行的真实宽度 {need.Width}（差 {need.Width - boxW} 像素）：" +
            "全角字一个占两格，宽度只要还按「字符数 × 西文单字宽」估，含全角字那行就会被从中间截断" +
            "（真窗口 150% 上「分阶段 …（其中重新分配缓冲区 1 次）」就是只剩前半句）");
        // 尺寸比完再数一遍墨。出界的字是被 GDI <b>裁掉</b>的（不会画到框外，窄框那一格实测框外恒为 0），
        // 所以"框外没墨"证不了"字都在"——只能比"这块框画出的墨"与"给足地方画出的墨"是否一字不差。
        // 真窗口 150% 那一档看到的正是这个失效：14 行读数只画出前 10 行。
        int inkBox = CountInk(text, boxW, boxH, StitchCanvasControl.HudPanelFlags);
        int inkFree = CountInk(text, boxW + 400, boxH + 400, StitchCanvasControl.HudPanelFlags);
        Battery.Assert(inkBox > 0, $"面板一个字都没画出来（{boxW}×{boxH}）——那这格的墨数对照无从谈起");
        Battery.Assert(inkBox == inkFree,
            $"{shape}：面板这块 {boxW}×{boxH} 只画出 {inkBox} 个墨点，给足地方能画出 {inkFree} 个" +
            $"（少了 {inkFree - inkBox}）⇒ 有读数被裁在框外，屏上少的那几行就是需求四点名的那几项");
        Battery.Evidence($"{shape}：面板 {boxW}×{boxH}、文字真实占位 {need.Width}×{need.Height}" +
                         $"（{lines} 行、每行 {need.Height / Math.Max(1, lines)} 像素）、" +
                         $"墨数 {inkBox}={inkFree} ⇒ 尺寸与像素两头都装得下");
        return text;
    }

    /// <summary>
    /// 精度红线 §3：上面那格对<b>宽度</b>的反证，与行距那一格是同一家族的第二个缺陷。
    /// <list type="bullet">
    ///   <item>机制：全角字比西文单字宽——不成立的话"按字符数估宽"根本不会出错，反证空转；</item>
    ///   <item>旧写法在"没选中拼缝"那一档<b>估窄了</b>：这一档就是真窗口 150% 上被截的那一屏，
    ///         差出的像素当场读出来，够不够一整行也当场读出来；</item>
    ///   <item>旧写法在"选中一条缝"那一档<b>只差几个像素</b>，被分档向上取整抹平——
    ///         这一句钉的是"常驻判据为什么当时是绿的"这件事本身。
    ///         下一轮不许再拿"canvas 那格是绿的"当"宽度没问题"的证据。</item>
    /// </list>
    /// 与行距那一格同一条边界：这一格证的是<b>机制与差值</b>，
    /// "这台机器的 150% 屏上真的截了"那半句的证据是真窗口那一跑 + 同源剪贴板读数的对照。
    /// </summary>
    private static void CharCountWidthWouldClipCjkLines()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        System.Threading.Thread.Sleep(150);          // 等过面板那 10Hz 闸门，两档才各量各的文本
        h.Render();
        string seamShape = h.Canvas.HudTextForTest;
        h.Canvas.SelectSeam(null);
        System.Threading.Thread.Sleep(150);
        h.Render();
        string idleShape = h.Canvas.HudTextForTest;
        Battery.Assert(!seamShape.Contains("没选中拼缝") && idleShape.Contains("没选中拼缝"),
            "夹具自证：这一格比的必须是两种读数形状（选中档不含「没选中拼缝」那句、没选中档含）。" +
            "两串一样就说明闸门挡住了第二档，那下面的估窄/够用两句量的就都是同一档");

        int asciiAdvance = StitchCanvasControl.HudTextExtent("MMMMMMMMMM").Width / 10;
        int cjkAdvance = StitchCanvasControl.HudTextExtent("中中中中中中中中中中").Width / 10;
        Battery.Assert(cjkAdvance >= asciiAdvance * 3 / 2,
            $"同一个 DPI 下量到：西文单字 {asciiAdvance} 像素、全角单字 {cjkAdvance} 像素。" +
            "全角字要是跟西文一样宽，「字符数 × 西文单字宽」就不会估窄，这一格的全部前提都不成立");

        (int boxIdle, int realIdle) = StitchCanvasControl.HudWidthsForTest(idleShape);
        (int boxSeam, int realSeam) = StitchCanvasControl.HudWidthsForTest(seamShape);
        Battery.Assert(boxIdle < realIdle,
            $"「没选中拼缝」这一档：旧写法（连 32 像素分档一起）得到的框 {boxIdle}，真实要 {realIdle}——" +
            "它要还装得下，上面那格的宽度断言就成了恒真，真窗口看到的截断也就得另有病因");
        Battery.Assert(boxSeam >= realSeam,
            $"「选中一条缝」这一档：旧写法的框 {boxSeam} 本该刚好够（真实要 {realSeam}）——" +
            "这一句钉的是「常驻判据当时为什么全绿」：那一档差不到一个分档步长，被向上取整掩盖。" +
            "它要是也装不下，就说明改造前那一档本该红而没红，得先查判据有没有真跑到这一档");
        Battery.Evidence($"旧写法（最长行字符数 × 西文单字宽 {asciiAdvance}，再向上取到 32 的倍数）：" +
                         $"「没选中拼缝」框 {boxIdle} < 真实 {realIdle} ⇒ 差 {realIdle - boxIdle} 像素" +
                         $"（最长行 {idleShape.Split('\n').Max(l => l.Length)} 个字符里含 " +
                         $"{idleShape.Split('\n').OrderByDescending(l => l.Length).First().Count(c => c > 0x2000)} 个全角字）" +
                         "⇒ 这一档常驻判据改造前就会红；" +
                         $"「选中一条缝」框 {boxSeam} ≥ 真实 {realSeam} ⇒ 差不到一个分档步长，被向上取整掩盖" +
                         "（这就是常驻判据当时全绿的形状）。" +
                         $"全角单字 {cjkAdvance} 像素 = 西文的 {(double)cjkAdvance / Math.Max(1, asciiAdvance):F1} 倍；" +
                         "同一档换到 150% 的屏上，差值按 DPI 放大——真窗口那半行不见了就是这么来的");
    }

    /// <summary>
    /// 精度红线 §3：上面那格的反证。<b>不改真实源文件</b>，把"行数 × 写死的 16 像素"这条
    /// <b>改造前</b>的算法与"同一支字体在这个 DPI 下每行多高"放在一起量，读出三件事：
    /// <list type="bullet">
    ///   <item>96 DPI：旧写法的框<b>本来就装得下</b>（行距只有 14，比那个 16 还小）——
    ///         这一句钉的是"电池 186 格全绿而缺陷一直活着"这件事本身，
    ///         下一轮不许再拿"canvas 那格是绿的"当"没有缺陷"的证据；</item>
    ///   <item>288 DPI：行距按算术翻上去，旧写法的框（不随 DPI 动）立刻装不下，
    ///         差出多少像素、折合整行切掉几行，当场读出来；</item>
    ///   <item>行距必须真的跟着 DPI 翻——不然两档比的是同一个数，反证空转。</item>
    /// </list>
    /// ═══ 为什么这一格不走 <c>TextRenderer.MeasureText</c> ═══
    /// 试过：给位图 <c>SetResolution(288,288)</c> 之后 MeasureText 量出来的字高<b>一字不变</b>
    /// （两档都是 196）——GDI 排版只认进程 DPI，而电池进程是 DPI 无感知的（只有产品进程声明了 DPI 感知）。所以这里只能走 <c>Font.GetHeight(Graphics)</c> 这条纯算术的路：
    /// 它跟着 <c>Graphics.DpiY</c> 走，才量得出"DPI 一变这字就得变大"。
    /// 这也意味着<b>这一格证的是"旧写法在高分屏会切行"这个机制，不是"这台机器的高分屏上真的切了行"</b>——
    /// 后半句的证据是真窗口那一跑（14 行只剩前 10 行），两件事不许混着写。
    /// </summary>
    private static void FixedLineHeightWouldClipOnHiDpi()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();
        string text = h.Canvas.HudTextForTest;
        int lines = text.Count(c => c == '\n') + 1;

        (int box96, int pitch96) = StitchCanvasControl.HudHeightsAtDpiForTest(text, 96f);
        (int box288, int pitch288) = StitchCanvasControl.HudHeightsAtDpiForTest(text, 288f);
        int need96 = lines * pitch96, need288 = lines * pitch288;

        Battery.Assert(pitch288 >= pitch96 * 2,
            $"同一支字体：96 DPI 每行 {pitch96} 像素、288 DPI 每行 {pitch288} 像素。DPI 翻 3 倍而行距没跟着翻，" +
            "说明这个量法不跟 DPI 走，那这一格与上面那格都是空判据");
        Battery.Assert(box96 == box288,
            "「行数 × 写死的 16」这条旧算法在两种 DPI 下给出的框高必须一模一样" +
            $"（{box96} vs {box288}）——它要是也随 DPI 变，被反证的就不是旧写法了");
        Battery.Assert(box96 >= need96,
            $"96 DPI 下旧写法的框 {box96} 本来就该装得下（{lines} 行 × {pitch96} = {need96}）——" +
            "这一句是「电池为什么量不到这个缺陷」的自证；它要是红了，说明连 96 DPI 都在切行，" +
            "真窗口那个形状就得另有病因");
        Battery.Assert(box288 < need288,
            $"288 DPI 下 {lines} 行要 {need288} 像素、旧写法的框只有 {box288}——这一格的全部意义就是" +
            "「行距一旦超过那个写死的 16，旧写法就整行切掉最后几行」；它不成立就说明这个机制不成立");
        int lost = (need288 - box288) / Math.Max(1, pitch288);
        Battery.Evidence($"旧写法（行数×16）：96 DPI 框 {box96} ≥ 需要 {need96} ⇒ 装得下（电池全绿却仍有缺陷，就是这个形状）；" +
                         $"288 DPI 框 {box288} < 需要 {need288} ⇒ 整行切掉 {lost} 行。" +
                         "真窗口 150% 那一档实测：14 行只剩前 10 行（帧耗时／HUD 单独耗时／分阶段／Paint 计数四行不见了）");
    }

    // ─────────────────── ⓪c 面板缓冲区复用（需求三第 3 条的另一半） ───────────────────

    /// <summary>
    /// 需求三第 3 条"文字面板使用缓存位图"到这里才算真的兑现：面板不只是"缓存了一张位图"，
    /// 而是<b>重画的时候复用同一张</b>。
    ///
    /// ═══ 改造前是什么形状 ═══
    /// 每次重建都 `_hudPanel?.Dispose(); _hudPanel = new Bitmap(w, h, Format32bppPArgb)`。
    /// 这张面板约 600×200，一个像素 4 字节 = 半 MB 以上，**已经越过 85KB 的大对象门槛**，
    /// 于是 10Hz 的闸门等于每秒往大对象堆里扔几 MB。那笔钱不体现在重画这一帧的毫秒数上，
    /// 而是体现在<b>随后那次回收把整帧顶到几十毫秒</b>——这正是需求五点名要区分"代码在算"
    /// 与"回收/抢占撞上来"的那种形状，光看帧耗时分不清，只能把"做没做这件事"记成计数。
    ///
    /// ═══ 三条读数各管一头，缺一条这格就成了空判据 ═══
    /// ① <b>第一帧必须恰好分配 1 次</b>：否则"分配计数"可能压根没接在重画那条路上，
    ///    那么下面那句"0 次"就永远绿（本项目反复栽过的那一族：计数器没接上）。
    /// ② <b>尺寸没跨档的连续重画一次都不许再分配</b>：这一条才是"复用"本身。
    ///    先核对面板长宽一字未变，再判 0 次——面板宽度按最长行算，跨一档就<b>该</b>重新分配
    ///    （那张缓冲区装不下了），把"允许跨档那一次"和"每次都分配"混进同一个数就说不清了。
    /// ③ <b>复用的那张内容必须真的变过</b>：不然"复用"退化成"留着上一帧的字"，
    ///    那是需求一点名过的"参数恢复了屏上数字不动"那一族骗人界面，比慢更糟。
    ///    比的是<b>缓冲区自己的字节</b>，不是屏上整帧——整帧里底下的画布也在动，证不了这一件事。
    /// </summary>
    private static void PanelRebuildReusesTheBuffer()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();                                       // 第一帧：这里必须分配第一张缓冲区
        Battery.AssertEqual(1L, h.Canvas.HudPanelAllocations,
            "开着读数层画第一帧：面板缓冲区必须恰好分配 1 次。0 次＝这个计数没接在重画那条路上" +
            "（那下面那句「一次都不许再分配」就成了恒真的白判据），多于 1 次＝一次重建分配了多张");

        long allocBase = h.Canvas.HudPanelAllocations;
        long rebuildBase = h.Canvas.HudPanelRebuilds;
        (int wBefore, int hBefore) = h.Canvas.HudPanelSizeForTest;
        using System.Drawing.Bitmap? first = h.Canvas.HudPanelSnapshotForTest();

        // 连画四帧：每帧之间跨过 100ms 闸门，视口来回各挪 1 行（300→301→300→301→300）。
        // 挪 1 行是让读数每帧都变（文本没变根本不会重建，那一档由闸门那格单独量），
        // 而 1 行既不改任何一行数字的位数、也撑不出 32px 的一档 ⇒ 这四帧走的全该是复用。
        for (int i = 0; i < 4; i++)
        {
            System.Threading.Thread.Sleep(105);
            int dy = i % 2 == 0 ? 1 : -1;
            h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft + new IntPoint(0, dy));
            h.Render();
        }

        long rebuilds = h.Canvas.HudPanelRebuilds - rebuildBase;
        long allocs = h.Canvas.HudPanelAllocations - allocBase;
        (int wAfter, int hAfter) = h.Canvas.HudPanelSizeForTest;
        Battery.Assert(rebuilds >= 4,
            $"夹具自证：这 4 帧各越过了 100ms 闸门、文本也在变，面板至少该重建 4 次，实际 {rebuilds} 次" +
            "——它要是 0 次，下面那句「分配 0 次」就只是「什么都没做」");
        Battery.Assert(wBefore == wAfter && hBefore == hAfter,
            $"夹具自证：这四帧面板尺寸该一字未变（跨档就名正言顺要重新分配），" +
            $"实际 {wBefore}×{hBefore} → {wAfter}×{hAfter}。要修的是这里的挪量，" +
            "别把下面那条改成「允许分配几次」——那样复用就又不被量了");
        Battery.AssertEqual(0L, allocs,
            $"尺寸一字未变、连重画 {rebuilds} 次，却重新分配了 {allocs} 张缓冲区：" +
            "每次重建都新分配一张半 MB 的位图就是往大对象堆里扔，代价落在随后那次回收顶起来的帧上");

        using System.Drawing.Bitmap? last = h.Canvas.HudPanelSnapshotForTest();
        int diffBytes = first is not null && last is not null ? CountPanelBytesDiffer(first, last) : -1;
        Battery.Assert(diffBytes > 0,
            $"复用同一张缓冲区之后，它的内容必须跟着新读数变（实际比出 {diffBytes} 个字节不同，" +
            "-1 是两张快照尺寸都不一致）：一字节都不变就是「复用」退化成了「留着上一帧的字」");
        Battery.Evidence($"重画 {rebuilds} 次、分配 {allocs} 次（第一帧那一次不计），尺寸稳在 {wAfter}×{hAfter}，" +
                         $"缓冲区内容有 {diffBytes} 个字节不同 ⇒ 复用是真的重画，不是不画");
    }

    /// <summary>
    /// 精度红线 §3：给上面那条配一格反证，证的是<b>计数本身分得清两条路</b>。
    /// 假如 <c>HudPanelAllocations</c> 其实在别处涨、或者恒为 0，那"复用那一格读到 0"什么也说明不了。
    /// 这里走产品通路不经过的出口 <c>RebuildHudPanelByAllocatingForTest</c>
    /// （每次都先释放旧的再分配一张——就是改造前的形状），连走三次，分配计数必须跟着涨 3。
    /// </summary>
    private static void AllocationCountingDistinguishesPolicies()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();
        Battery.AssertEqual(1L, h.Canvas.HudPanelAllocations,
            "正向对照：产品通路画第一帧就该有 1 次分配（没有就说明这计数不接在重画那条路上）");

        for (int i = 0; i < 3; i++) h.Canvas.RebuildHudPanelByAllocatingForTest();
        Battery.AssertEqual(3L, h.Canvas.HudPanelAllocations - 1,
            "按旧写法（每次重建都释放再分配）连重画三次，分配计数必须涨 3。它要是不涨，" +
            "canvas 那一格读到的 0 就只是「计数没接上」，判据永远绿——比没写更坏");
        Battery.AssertEqual(3L, h.Canvas.HudPanelRebuilds - 1,
            "旧写法那三次也得各记一次重建：重建与分配本来就是两个数" +
            "（复用那条路是「重建涨、分配不涨」，两个数合成一个就分不出这两种形状）");
        Battery.Evidence("旧写法 3 次 ⇒ 分配 +3、重建 +3；现行产品通路同尺寸重画 4 次 ⇒ 分配 +0、重建 +4");
    }

    /// <summary>
    /// 把两张面板快照逐字节相比，数出不同的字节数；尺寸不同就返回 -1（让调用方的断言直接红并报到数上）。
    /// 用 <c>LockBits</c> 一次拷出来比，不用 <c>GetPixel</c>：半 MB 逐像素调用会把一格判据跑成几秒。
    /// </summary>
    internal static int CountPanelBytesDiffer(System.Drawing.Bitmap a, System.Drawing.Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return -1;
        var rect = new System.Drawing.Rectangle(0, 0, a.Width, a.Height);
        System.Drawing.Imaging.BitmapData da =
            a.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, a.PixelFormat);
        System.Drawing.Imaging.BitmapData db =
            b.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, b.PixelFormat);
        try
        {
            byte[] ba = new byte[da.Stride * a.Height];
            byte[] bb = new byte[db.Stride * b.Height];
            System.Runtime.InteropServices.Marshal.Copy(da.Scan0, ba, 0, ba.Length);
            System.Runtime.InteropServices.Marshal.Copy(db.Scan0, bb, 0, bb.Length);
            int diff = 0;
            for (int i = 0; i < ba.Length; i++) if (ba[i] != bb[i]) diff++;
            return diff;
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }

    /// <summary>
    /// 逐<b>像素</b>相比（四通道任一不同算一个像素），并交出<b>单通道最大偏差</b>。
    /// 第三十改要它是因为那个偏差数能分开两种完全不同的差：最大偏差 1 是"半透明在透明层上合成两次、
    /// 第二次取整偏了一格"的量化形状（8-bit 双取整的理论上限），最大偏差几十则是"画错了/少画了一层"。
    /// 只有字节数没有偏差数，下一轮就分不清这两件事，也就没资格决定该不该重开那个判断。
    /// 尺寸不同直接抛（让调用方红在数上而不是红在"0 个像素不同"上）。
    /// </summary>
    internal static int DiffArgbPixels(System.Drawing.Bitmap a, System.Drawing.Bitmap b, out int maxChannelDeviation)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new InvalidOperationException($"两张帧尺寸不同：{a.Width}×{a.Height} vs {b.Width}×{b.Height}");
        var rect = new System.Drawing.Rectangle(0, 0, a.Width, a.Height);
        System.Drawing.Imaging.BitmapData da =
            a.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, a.PixelFormat);
        System.Drawing.Imaging.BitmapData db =
            b.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, b.PixelFormat);
        try
        {
            byte[] ba = new byte[da.Stride * a.Height];
            byte[] bb = new byte[db.Stride * b.Height];
            System.Runtime.InteropServices.Marshal.Copy(da.Scan0, ba, 0, ba.Length);
            System.Runtime.InteropServices.Marshal.Copy(db.Scan0, bb, 0, bb.Length);
            int pixels = 0, max = 0;
            for (int y = 0; y < a.Height; y++)
            {
                int rowA = y * da.Stride, rowB = y * db.Stride;
                for (int x = 0; x < a.Width; x++)
                {
                    int ia = rowA + x * 4, ib = rowB + x * 4;
                    bool different = false;
                    for (int c = 0; c < 4; c++)
                    {
                        int d = Math.Abs(ba[ia + c] - bb[ib + c]);
                        if (d == 0) continue;
                        different = true;
                        if (d > max) max = d;
                    }
                    if (different) pixels++;
                }
            }
            maxChannelDeviation = max;
            return pixels;
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }

    // ───────────────────────── ⓪b 五个触发量（需求三第 4 条） ─────────────────────────

    /// <summary>
    /// 需求三第 4 条点名了五个量：Viewport.TopLeft、Zoom、AlignmentOffset、CutY、OverlapRect
    /// ——"只有这些变化才重绘"。这句话的两个半边都要能红：
    /// <list type="bullet">
    ///   <item><b>只有这些变化才重绘</b>（不许多余重绘）= <c>IdleFramesRebuildOnlyOnce</c> 里
    ///         "静止 20 帧一次都不重算"那一格；</item>
    ///   <item><b>这些变了必须重绘</b>（不许漏）= 本格。漏掉任何一个量，屏幕上就会留着
    ///         上一帧的位置——而"多余重绘"只是慢，"漏重绘"是错，所以这一半必须有名字。</item>
    /// </list>
    ///
    /// ═══ 两条诚实的限制，写在读数控言里 ═══
    /// ① 视口与缩放是**纯显示量**：改它们不动修订号，所以这两条是严格的——
    ///    哪个没进缓存签名，这格当场就红。
    /// ② 对齐、切点、重叠带只能经 <c>Project.Mutate</c> 改，而签名里含修订号（参数一改就重算，
    ///    哪怕看不出来）。所以那三条量的是"改完确实重算了"，**量不到**"只有它能触发"。
    ///    本项目的模型本来就只剩 <c>AlignmentOffset</c> 与 <c>CutY</c> 两个状态量
    ///    （<c>OverlapRect</c> 由它们派生），所以需求那五格里"对齐"与"重叠带"在实现里同源，
    ///    这一条如实记在这里，不假装它们是两次独立的改动。
    /// </summary>
    private static void EachNamedTriggerInvalidatesOverlay()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();                                                   // 把两套缓存建起来

        var report = new List<string>();

        // ① 视口原点（纯显示量，不动修订号）
        h.Render();                                                   // 先证静止不重算
        long idle = h.Canvas.HudGeometryRebuilds;
        h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft + new IntPoint(0, 7));
        h.Render();
        Battery.Assert(h.Canvas.HudGeometryRebuilds > idle,
            "改了 Viewport.TopLeft 却没重算叠层几何 ⇒ 缓存签名漏了这一项，屏幕上会留着上一帧的位置");
        report.Add($"视口 +7 行：重算 {h.Canvas.HudGeometryRebuilds - idle} 次");

        // ② 缩放（同样不动修订号）
        h.Render();
        idle = h.Canvas.HudGeometryRebuilds;
        h.Canvas.Viewport.SetZoom(0.9);
        h.Render();
        Battery.Assert(h.Canvas.HudGeometryRebuilds > idle,
            "改了 Zoom 却没重算叠层几何 ⇒ 同一个漏法：换算出来的设备矩形是按旧缩放算的");
        report.Add($"缩放 1.0→0.9：重算 {h.Canvas.HudGeometryRebuilds - idle} 次");

        // ③④⑤ 三个参数量（改法只有 Mutate 这一条通路，见上面限制 ②）
        idle = h.Canvas.HudGeometryRebuilds;
        h.P.Mutate("改对齐（重叠）", _ => h.Current.SetOverlap(Overlap - 20));
        h.Render();
        Battery.Assert(h.Canvas.HudGeometryRebuilds > idle,
            "改了对齐（本模型里就是重叠长度）必须重算叠层几何——AlignmentOffset 与 OverlapRect " +
            "同源，这一趟同时覆盖需求那两格");
        report.Add($"对齐／重叠带 −20：重算 {h.Canvas.HudGeometryRebuilds - idle} 次");

        idle = h.Canvas.HudGeometryRebuilds;
        h.P.Mutate("改蓝线", _ => h.Current.ApplyCut(40));
        h.Render();
        Battery.Assert(h.Canvas.HudGeometryRebuilds > idle,
            "改了 CutY（蓝线）必须重算叠层几何：那块命中区与箭头把手都按切点摆");
        report.Add($"蓝线切点 →40：重算 {h.Canvas.HudGeometryRebuilds - idle} 次");

        // 面板那一路也照需求第 2 条跟着变（数字变了就得赶上下一个 100ms 窗口）
        string textBefore = h.Canvas.HudTextForTest;
        System.Threading.Thread.Sleep(150);
        h.Render();
        Battery.Assert(!h.Canvas.HudTextForTest.Equals(textBefore, StringComparison.Ordinal),
            "参数改完之后读数文本必须跟着变——屏上数字停在旧值就是需求一点名的那类骗人界面");
        report.Add($"面板文本已跟随（重建累计 {h.Canvas.HudPanelRebuilds} 次）");
        Battery.Evidence(string.Join("；", report));
    }

    // ─────────────────── ⓪d 描边裁到可见区（需求三那 7ms 的真正出处） ───────────────────

    /// <summary>
    /// ═══ 这一格是本轮量出来的那个大头 ═══
    /// 读数层整段以前每帧约 7.9ms，拆账之后 <b>八成多都在"描边"这一笔</b>：
    /// <c>DrawRectangle(虚线笔, 画布边界)</c> 里那个矩形在设备坐标可以有上万像素高
    /// （成品 41532 行、缩放 0.35 也是 14537 像素），而屏上只看得到其中一屏。
    /// <b>虚线的相位是沿整条路径量的</b>，GDI+ 不会因为屏幕裁剪就跳过看不见的部分——
    /// 于是每帧沿着几万画素长的路径排一遍虚线，只为了画出其中一根 500 像素长的框。
    /// 裁到可见区之后同一批帧的实测：读数层整段 7.9 → 2.2ms/帧，开读数层那一档的 P95 36.9 → 29.4ms。
    ///
    /// ═══ 判什么 ═══
    /// ① 裁这条<b>语义</b>：一条 9600 像素高的矩形在 500 高的可见区里必须被裁短（且仍覆盖整个可见高度），
    ///    整条都在屏幕外的必须返回 null（一笔都不该下）；
    /// ② 产品通路<b>真的走了裁</b>：开着读数层画 3 帧，裁边计数必须 +3；
    ///    关着画 2 帧必须 +0（否则这个数可能是别处涨的，那句 +3 就成了恒真）。
    /// 屏上看不出裁与不裁的区别（屏幕外那几万像素本来就看不见），所以这件事<b>只能靠计数钉住</b>——
    /// 与贴图那一格同一个道理。代价的反证在 <c>guard/描边若不裁到可见区则虚线路径代价翻红</c>。
    /// </summary>
    private static void OutlineIsClippedToTheVisibleArea()
    {
        var visible = new Rectangle(0, 0, 400, 500);
        var tall = new Rectangle(0, -9000, 60, 9600);        // 一条从屏幕外几千像素延伸进来的画布边界
        Rectangle? clipped = StitchCanvasControl.ClipOverlayForTest(visible, tall, 1f);
        Battery.Assert(clipped is not null,
            "一条与可见区相交的大矩形不该被裁成 null（那就是把看得见的那段框也弄丢了）");
        int clippedHeight = clipped!.Value.Height;
        Battery.Assert(clippedHeight < tall.Height / 4,
            $"裁完仍有 {clippedHeight} 高，原矩形 {tall.Height} 高——没裁短就等于没省：" +
            "虚线要沿整条路径排相位，路径长度就是这一笔的代价");
        Battery.Assert(clipped!.Value.Y <= 0 && clipped.Value.Bottom >= visible.Height,
            $"裁出来的那段必须仍盖住整个可见高度（实际 y={clipped.Value.Y}..{clipped.Value.Bottom}）：" +
            "屏上那条灰框不许因为裁短而缺一段");

        Rectangle? offscreen = StitchCanvasControl.ClipOverlayForTest(visible,
            new Rectangle(0, 5000, 60, 400), 1f);
        Battery.Assert(offscreen is null,
            "整条都在屏幕外的矩形必须返回 null（一笔都不许下）——现在拿到了 " +
            (offscreen?.ToString() ?? "null"));

        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        long baseClamped = h.Canvas.HudOutlinesClamped;
        for (int i = 0; i < 3; i++) h.Render();
        Battery.AssertEqual(3L, h.Canvas.HudOutlinesClamped - baseClamped,
            "开着读数层连画 3 帧：这 3 帧的描边都得走「裁到可见区」那一条路（这个计数只在裁过的那一段涨）");

        h.Canvas.ShowDebugHud = false;
        long afterOn = h.Canvas.HudOutlinesClamped;
        for (int i = 0; i < 2; i++) h.Render();
        Battery.AssertEqual(afterOn, h.Canvas.HudOutlinesClamped,
            "关着读数层画 2 帧：一笔调试描边都不该下——不然上面那句 +3 可能只是别处涨上来的");
        Battery.Evidence($"9600 高的矩形裁到 {clippedHeight} 高、屏幕外那条返回 null；" +
                         "开 3 帧 ⇒ 裁边 +3、关 2 帧 ⇒ +0");
    }

    /// <summary>
    /// 精度红线 §3：上面那格的代价反证。<b>不改真实源文件</b>，在内存里把两种画法各跑一遍：
    /// 同一条虚线笔、同一块 400×500 画布，一次画<b>整条</b> 9600 像素高的大矩形（改造前的形状），
    /// 一次画裁到可见区的那一段。要求前者明显更贵——不然"裁不裁"就是一件无所谓的事，
    /// 常驻那格钉的计数也就成了空转。
    /// 这一格判的是<b>比值</b>而不是绝对毫秒：机器快慢会变，路径长度决定的那笔差距不会变。
    /// </summary>
    private static void UnclippedOutlineWouldCostTheWholePath()
    {
        const int rounds = 10;
        var visible = new Rectangle(0, 0, 400, 500);
        // 与真窗口里那一档同量级：成品 41532 行、缩放 0.35，画布边界的设备高就是 14537 像素。
        // 拿 30000 这条（更长）跑比值，比值越大越不容易被机器噪声抹平——判的是比值，不是绝对毫秒。
        var tall = new Rectangle(0, -29000, 60, 30000);
        Rectangle clippedOnce = StitchCanvasControl.ClipOverlayForTest(visible, tall, 1f)!.Value;
        using var dash = new System.Drawing.Pen(System.Drawing.Color.Gray, 1f)
        { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };

        using var probe = new System.Drawing.Bitmap(400, 500);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(probe))
        {
            g.Clear(System.Drawing.Color.Black);
            for (int i = 0; i < 3; i++) g.DrawRectangle(dash, tall);   // 预热：让第一条的建路径/JIT 不计入
        }

        long wholeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(probe))
            for (int i = 0; i < rounds; i++) g.DrawRectangle(dash, tall);
        double wholeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - wholeStart) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;

        long clippedStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(probe))
            for (int i = 0; i < rounds; i++) g.DrawRectangle(dash, clippedOnce);
        double clippedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - clippedStart) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;

        Battery.Assert(wholeMs >= clippedMs * 5,
            $"沿整条路径画 {rounds} 次只花 {wholeMs:F1}ms、裁到可见区花 {clippedMs:F1}ms（比值 " +
            $"{wholeMs / System.Math.Max(0.001, clippedMs):F1}）——差距不到 5 倍就说明这一笔本来就不贵，" +
            "常驻那格钉的计数是空转，得回去重看裁切到底省了什么");
        Battery.Evidence($"同样 {rounds} 次虚线描边：整条 30000 高 {wholeMs:F1}ms vs 裁到可见区 {clippedMs:F1}ms" +
                         $"（{wholeMs / System.Math.Max(0.001, clippedMs):F1} 倍）" +
                         "⇒ 路径长度就是这笔的代价，裁它不是可有可无的写法偏好。真窗口那批的拆账同向：" +
                         "描边每帧 7.2 → 1.3ms，读数层整段 7.9 → 2.2ms");
    }

    // ─────────── ⓪e 角标与面板：框装不下字时字是被「裁掉」的，所以数墨不许变少 ───────────

    /// <summary>
    /// 真窗口那一跑还看到画布上那几块跟着鼠标的字块（拖动角标、点选切分引导线）像是半句话被截断。
    /// 这一格把它量成数：<b>按产品的算法算出框、按产品那套 flags 真画一遍，数画出了多少墨</b>，
    /// 再与"给一块大到装得下任何写法的画布"画出来的墨数比——两个数必须一字不差。
    ///
    /// ═══ 为什么比"墨数相等"而不是"框外不许有墨" ═══
    /// 一开始写的是后者，跑出来是<b>假绿</b>：把框故意做窄一半，框外仍读到 0 个墨点。
    /// 量出来的事实是 <b>GDI 把出界的字裁掉，而不是画到框外</b>（这一族反证那一格现在把这两个数都读出来钉着）。
    /// 于是"框外没墨"证不了"字都在"——字不见了恰恰表现为<b>框内墨变少</b>。
    /// F8 面板那处缺陷就是这个形状：14 行读数只画出前 10 行，屏上少了字而"尺寸比较"看不出来。
    /// 判据：<c>canvas/画布角标的框必须装得下它那行字</c>，
    /// 反证：<c>guard/角标的框故意做窄则墨一定变少</c>。
    /// </summary>
    private static void BadgeBoxContainsItsOwnText()
    {
        string[] texts =
        {
            "对齐：重叠 12345 行（画布会变高变矮，蓝线跟着重新夹取）",
            "蓝线 -12345 行（总重叠不变，X 不参与）",
            "切分线 Y=12345（源图）· 单击切分，Esc 取消",
        };
        using System.Drawing.Bitmap probe = new(16, 16);
        using System.Drawing.Graphics pg = System.Drawing.Graphics.FromImage(probe);
        var report = new System.Text.StringBuilder();
        foreach (string text in texts)
        {
            Size need = StitchCanvasControl.BadgeTextSize(pg, text);
            int boxW = need.Width + 12, boxH = need.Height + 6;      // 与产品那两处一字同
            int ink = CountInk(text, boxW, boxH, StitchCanvasControl.BadgeFlags);
            int inkFree = CountInk(text, boxW + 400, boxH + 400, StitchCanvasControl.BadgeFlags);
            Battery.Assert(ink > 0, $"这行字一个像素都没画出来（框 {boxW}×{boxH}）：{text}");
            Battery.Assert(ink == inkFree,
                $"角标的框 {boxW}×{boxH} 里只画出 {ink} 个墨点，而给足地方能画出 {inkFree} 个" +
                $"（少了 {inkFree - ink}）⇒ 有字被裁在框外，屏上就是半句话：" + text);
            report.Append($"[{boxW}×{boxH} {ink}={inkFree}] ");
        }
        Battery.Evidence("三句角标（含全角括号与中日韩）：产品框内画出的墨与「给足地方」画出的墨一字不差 " + report);
    }

    /// <summary>
    /// 精度红线 §3：上面那格的反证。<b>故意把框做窄一半</b>再画一次，要求墨必须变少。
    /// 它不变少只有两种可能：① 计数在空转（正面那格量的不是字）；② 这行字的宽度跟框宽无关
    /// （那正面那格就永远抓不到"字被裁"）。顺带把"框外墨点恒为 0"这件事也量出来——
    /// 这正是本族判据必须数墨、不能数"框外有没有墨"的理由，写在证据里免得下一轮又走回头路。
    /// </summary>
    private static void NarrowBadgeBoxMustLoseInk()
    {
        const string text = "对齐：重叠 12345 行（画布会变高变矮，蓝线跟着重新夹取）";
        using System.Drawing.Bitmap probe = new(16, 16);
        using System.Drawing.Graphics pg = System.Drawing.Graphics.FromImage(probe);
        Size need = StitchCanvasControl.BadgeTextSize(pg, text);
        int fullW = need.Width + 12, h = need.Height + 6;
        int inkFull = CountInk(text, fullW, h, StitchCanvasControl.BadgeFlags);
        int inkNarrow = CountInk(text, fullW / 2, h, StitchCanvasControl.BadgeFlags);
        int leaked = CountInk(text, fullW / 2, h, StitchCanvasControl.BadgeFlags, countOutside: true);

        Battery.Assert(inkFull > 0, "宽框一个像素都没画出来——两格的对照都无从谈起");
        Battery.Assert(inkNarrow < inkFull,
            $"框窄一半（{fullW / 2} vs {fullW}）之后框内墨点 {inkNarrow} 必须少于 {inkFull}——" +
            "它不少就说明这行字跟框宽无关，正面那格量的就不是「字有没有被裁」");
        Battery.Assert(leaked == 0,
            $"窄框的框外读到 {leaked} 个墨点——若真会溢出到框外，正面那格就该改成数「框外有没有墨」" +
            "而不是数「墨有没有变少」。这一句把当前 GDI 的行为（裁掉，不溢出）钉成读数");
        Battery.Evidence($"宽框 {fullW}×{h} 墨 {inkFull}；窄框 {fullW / 2}×{h} 墨 {inkNarrow}" +
                         $"（少 {inkFull - inkNarrow}）、框外 {leaked} ⇒ 出界的字是「消失」不是「溢出」" +
                         "⇒ 这一族判据一律数墨，不数框外");
    }

    /// <summary>
    /// 把一行字按<b>产品那两处用的同一套 flags</b>画进 (0,0,boxWidth,boxHeight) 这块框，
    /// 数画出来的墨。默认只数<b>框内</b>（因为出界的字会被 GDI 裁掉，数框内才看得见"少了字"）；
    /// <paramref name="countOutside"/> 为数框外——反证那一格用它来把"GDI 是裁掉不是溢出"这件事量出来。
    /// 画布比框四周各多留一截，溢出（若会）才落得下。走 LockBits + Marshal.Copy 整行读
    /// （GetPixel 逐点要慢一个量级）。
    /// </summary>
    private static int CountInk(string text, int boxWidth, int boxHeight, TextFormatFlags flags,
        bool countOutside = false)
    {
        const int pad = 400;
        int w = boxWidth + pad, h = boxHeight * 2 + pad;
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Black);
            TextRenderer.DrawText(g, text, Theme.MonoFont, new Rectangle(0, 0, boxWidth, boxHeight),
                System.Drawing.Color.White, flags);
        }
        int ink = 0;
        System.Drawing.Imaging.BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            byte[] row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < w; x++)
                {
                    int b = x * 4;
                    if (row[b] < 8 && row[b + 1] < 8 && row[b + 2] < 8) continue;   // BGRA，仍是黑底
                    bool insideBox = x < boxWidth && y < boxHeight;
                    if (insideBox != countOutside) ink++;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return ink;
    }

    // ───────────────────────── ① 不许把数据层拖进来 ─────────────────────────

    /// <summary>
    /// 需求三第 6 条逐字兑现：读数层<u>不得</u>调用 RenderWhole、完整 CanvasLayout.Build、
    /// 全量 TileCache 清理、全量图片解码。四个计数器各量一次增量，全为 0 才算过。
    /// 反向对照放在最后：把视口挪一帧，瓦片渲染数必须动起来——
    /// 不然"0 次渲染"可能只是"这个夹具压根没有东西可渲染"（本项目栽过多次的那类空判据）。
    /// </summary>
    private static void HudPaintNeverTouchesTheDataLayer()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();                                       // 预热：瓦片与面板都进缓存

        long renders = h.Canvas.TileRendersForTest;
        long clears = h.Canvas.TileFullInvalidationsForTest;
        long whole = CanvasComposer.WholeRunCountForTest;
        long builds = CanvasLayout.BuildCountForTest;
        for (int i = 0; i < 20; i++) h.Render();

        Battery.AssertEqual(0L, CanvasComposer.WholeRunCountForTest - whole,
            "开着读数层画 20 帧：一次 RenderWhole 都不许有（需求三第 6 条）");
        Battery.AssertEqual(0L, CanvasLayout.BuildCountForTest - builds,
            "开着读数层画 20 帧：一次完整 CanvasLayout.Build 都不许有——绘制只许读 Project.Layout 那份缓存");
        Battery.AssertEqual(0L, h.Canvas.TileFullInvalidationsForTest - clears,
            "开着读数层画 20 帧：TileCache 一次都不许整块清空");
        Battery.AssertEqual(0L, h.Canvas.TileRendersForTest - renders,
            "开着读数层画 20 帧：一块瓦片都不许重算（重算就等于再从源图取数，即需求三说的「全量图片解码」）");

        h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft + new IntPoint(0, 600));
        h.Render();
        Battery.Assert(h.Canvas.TileRendersForTest - renders > 0,
            $"夹具自证：视口一动就得重算瓦片（实际 {h.Canvas.TileRendersForTest - renders} 块）——" +
            "上面那四个 0 因此不是「这个夹具没东西可渲染」造成的");
        Battery.Evidence("读数层 20 帧：RenderWhole 0、完整布局重算 0、整块清缓存 0、瓦片重算 0；" +
                         "挪一次视口后瓦片重算立刻 > 0 ⇒ 对照组成立");
    }

    // ───────────────────────── ② 闸门：10Hz 与几何缓存 ─────────────────────────

    /// <summary>
    /// 需求三第 2、3、4、5 条合起来的那格：
    /// · 文字面板按 <b>10Hz 上限</b>重建——在不足 100ms 的窗口里连画 20 帧，只许重建 1 次；
    /// · 几何换算走签名缓存——静止 20 帧一次都不许重算；
    /// · 两条各配一个反向对照：改一次参数 / 挪一次视口，对应的计数必须动起来，
    ///   否则"只重建一次"可能只是"再也没重建过"（缓存不失效是另一类缺陷）。
    /// </summary>
    private static void IdleFramesRebuildOnlyOnce()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();                                       // 首帧建好面板与几何缓存

        long panel = h.Canvas.HudPanelRebuilds;
        long geo = h.Canvas.HudGeometryRebuilds;
        long start = Environment.TickCount64;
        for (int i = 0; i < 20; i++) h.Render();
        long elapsed = Environment.TickCount64 - start;
        long panelDelta = h.Canvas.HudPanelRebuilds - panel;
        long budget = 1 + elapsed / 100;                 // 10Hz 上限换算成这个窗口的允许次数
        Battery.Assert(elapsed < 200,
            $"夹具自证：这 20 帧要画得够快才量得出闸门（实际 {elapsed}ms）——太慢的话窗口跨了好几个 100ms，" +
            "上限本来就宽，判不出闸门有没有生效");
        Battery.Assert(panelDelta <= budget && h.Canvas.HudPanelRebuilds >= 1,
            $"静止 {elapsed}ms 里画了 20 帧：面板重建 {panelDelta} 次（上限 {budget} 次＝10Hz），" +
            $"全程累计 {h.Canvas.HudPanelRebuilds} 次（至少要有首帧那一次，否则是「什么都没画」不是「省下了」）。" +
            "退回「每帧都重排文本」会直接变成 20 次");
        // 0 次也是合法读数：静止时那 11 行文本一字没变（帧耗时都相同），连 10Hz 那一次都不该花。
        Battery.Assert(budget < 20, $"夹具自证：上限 {budget} 必须远小于帧数 20，否则这条判据是恒真的");

        Battery.AssertEqual(0L, h.Canvas.HudGeometryRebuilds - geo,
            "静止 20 帧：设备矩形一次都不许重算（需求三第 4 条：只有视口/缩放/对齐/切点/带变了才重绘）");

        // 反向对照 1：视口变了 ⇒ 几何必须重算
        h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft + new IntPoint(0, 3));
        h.Render();
        Battery.Assert(h.Canvas.HudGeometryRebuilds - geo > 0,
            "挪一点视口之后几何缓存必须重算——不重算就是「缓存永不失效」，屏上会留着上一帧的位置");

        // 反向对照 2：改了参数、且已过 100ms ⇒ 面板必须重建，读数不许停在旧值
        long panelBeforeChange = h.Canvas.HudPanelRebuilds;
        string textBefore = h.Canvas.HudTextForTest;
        h.P.Mutate("改重叠", _ => h.Current.SetOverlap(Overlap - 20));
        System.Threading.Thread.Sleep(150);
        h.Render();
        Battery.Assert(h.Canvas.HudPanelRebuilds > panelBeforeChange,
            "过了 100ms 又改了参数，面板必须再重建一次（闸门不许把读数冻住）");
        Battery.Assert(!h.Canvas.HudTextForTest.Contains("CutY=0/120") ||
                       !textBefore.Equals(h.Canvas.HudTextForTest),
            "读数文本必须跟着变——屏幕上的数字停在旧值，就是需求一点名的那类骗人界面");
        Battery.Evidence($"静止 {elapsed}ms / 20 帧：面板重建 {panelDelta} 次（上限 {budget}）、几何重算 0 次；" +
                         "视口一动、参数一改之后两者都跟着动");
    }

    // ───────────────────────── ③ 每帧堆分配 ─────────────────────────

    /// <summary>
    /// 需求三第 5 条"每帧不许新建 Brush / Font / 重排文本"的可机器核对形式：
    /// 同一段稳态绘制，<b>开着读数层比关着每帧多分配多少字节</b>。
    /// 上限取 512 字节/帧——一帧里新建一支 Pen 或一个 SolidBrush 就是几十字节，
    /// 拼一行字符串就是上百字节，"每帧五支笔 + 11 行文本"必然越过这条线（第 ④ 格把它复现了一遍）。
    ///
    /// 为什么量"差值"而不是"绝对值"：绘制通路本身每帧也要分配（分阶段耗时那个字符串等），
    /// 那部分与读数层无关，混在一个绝对数里就成了"谁都过不去"或者"阈值只能放很宽"。
    /// </summary>
    private static void HudAddsAlmostNoAllocation()
    {
        using Harness h = Harness.New();
        h.Canvas.ShowDebugHud = false;
        h.Render(); h.Render();                                     // 预热：瓦片、布局缓存都到位
        long offStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) h.Render();
        long offBytes = GC.GetAllocatedBytesForCurrentThread() - offStart;

        h.Canvas.ShowDebugHud = true;
        h.Render();                                                 // 首帧：建面板与几何缓存，不计入
        long onStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) h.Render();
        long hudBytes = GC.GetAllocatedBytesForCurrentThread() - onStart;

        double perFrameExtra = (hudBytes - offBytes) / 20.0;
        Battery.Assert(h.Canvas.HudSplitStats.Frames > 0,
            "夹具自证：这几帧真的开着读数层画过（否则下面那句「额外分配很小」只是「什么都没做」）");
        Battery.Assert(perFrameExtra <= 512,
            $"开着读数层每帧比关着多分配 {perFrameExtra:F0} 字节，超过 512 的上限——" +
            "基本就是「每帧又新建了笔/刷子，或者又排了一遍文本」");
        Battery.Evidence($"关着 20 帧共 {offBytes} 字节、开着 20 帧共 {hudBytes} 字节 ⇒ 每帧额外 " +
                         $"{perFrameExtra:F0} 字节（上限 512）；HUD 单独耗时 P95 " +
                         $"{h.Canvas.HudSplitStats.HudP95:F1}ms");
    }

    /// <summary>
    /// 精度红线 §3：给上面那条判据配一格"注缺陷"探针。
    /// 这里不改真实源文件，而是把<b>第十三轮之前那个写法</b>当场复现一遍：
    /// 每帧拼 11 行字符串 + MeasureText + 新建一支 Pen/Brush + 建一张位图重排文本。
    /// 要求它每帧的分配<b>明显越过</b> 512 字节那条线——越线越明显，说明常驻判据量的就是这件事。
    /// </summary>
    private static void OldPerFrameHudWouldBlowTheBudget()
    {
        const int frames = 20;
        using var probe = new System.Drawing.Bitmap(8, 8);
        using System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(probe);

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
        {
            // 旧写法的三件每帧开销：拼文本、量文本、建位图（笔与刷子也各 new 一份）
            var lines = new List<string>();
            for (int row = 0; row < 11; row++)
                lines.Add($"第 {row} 行 Rect1=(0,300,60,600) 绿 CutY=40/120 视口=(0,400) zoom=1.000 " +
                          $"瓦片 缓存3块 渲染{i} 整块清0 命中率 60%");
            string text = string.Join('\n', lines);
            int widest = 0;
            foreach (string line in lines)
                widest = Math.Max(widest, System.Windows.Forms.TextRenderer.MeasureText(
                    g, line, System.Drawing.SystemFonts.DefaultFont).Width);
            using var bmp = new System.Drawing.Bitmap(Math.Max(8, widest), 12 * 11 + 6);
            using (System.Drawing.Graphics tg = System.Drawing.Graphics.FromImage(bmp))
            {
                using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
                using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2f);
                tg.FillRectangle(fill, 0, 0, bmp.Width, bmp.Height);
                tg.DrawLine(pen, 0, 0, bmp.Width, bmp.Height);
                System.Windows.Forms.TextRenderer.DrawText(tg, text,
                    System.Drawing.SystemFonts.DefaultFont, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                    System.Drawing.Color.White);
            }
        }
        double perFrame = (GC.GetAllocatedBytesForCurrentThread() - start) / (double)frames;
        Battery.Assert(perFrame > 512,
            $"旧写法每帧只分配了 {perFrame:F0} 字节——变异没生效：那样常驻判据的 512 上限就成了白写的数");
        Battery.Evidence($"旧写法（每帧排文本+建位图+新建画笔）每帧约 {perFrame:F0} 字节，" +
                         $"是现行稳态上限（512）的 {perFrame / 512:F1} 倍 ⇒ 那条判据量得到这件事");
    }

    // ───────────────────────── 夹具 ─────────────────────────

    /// <summary>
    /// 一个"上不了屏但能真画一帧"的画布夹具：走的就是产品那条 OnPaint 通路。
    /// 对 <see cref="SessionDiagnosticsTests"/> 开放（internal）——同一批读数格子要的是同一个夹具，
    /// 复制一份就成了"两处各自定义什么叫稳态"，改一处漏一处。
    /// </summary>
    // ───────────────────── ⑦ 拖动段：只算按下之后的帧（需求五那条 `单帧 ≤ 50ms` 该判在哪一段时间上） ─────────────────────

    /// <summary>
    /// 这一格钉的是"拖动段那几笔读数的取数范围"。为什么产品要自己记住手势起点，
    /// 而不是让工装去数帧：<b>真窗口 150% 连跑 5 趟量到的"最慢帧"根本不是拖动帧</b>——
    /// 拖动前取一次读数（池里只有 2～4 枚样本，全在导入/首次绘制那几帧上）最慢是 71.7～83.7ms，
    /// 8 次拖动后再取（池里 177～189 枚）最慢<b>一字不差还是那一枚</b>；
    /// 样本池有 4096 格，一趟拖动转不出去，导入帧就一直留在池里。
    /// 于是"拿全池最慢去判需求五的单帧阈值"量的就是环境的钱。判据要能红，就得把
    /// <b>"段里的帧数"与"段里的样本必须是池子末尾那几枚"</b>这两件事钉死：
    ///
    /// · 没按过 ⇒ <see cref="StitchCanvasControl.HasGestureMark"/> 为假、段帧数 0，
    ///   出口那一行必须明写"还没按过"（不许把导入帧算成拖动帧）；
    /// · 按下＋移动 ⇒ 段起点落在产品自己的手势通路里（平移或拼缝拖动都算），
    ///   此后画 3 帧就是 3 帧，且这 3 枚必须<b>等于</b>整池的最后 3 枚（不是整池）；
    /// · 再按一次 ⇒ 段帧数<b>重新起算</b>成 5（不是 3+5、更不是整池）——这一笔同时钉住
    ///   "标记会被下一次手势挪走"，否则下一段会把上一段的帧混进来。
    ///
    /// 出口那一行（<c>拖动段（自最近一次按下起）N 帧</c>）里那个 N 必须就是
    /// <see cref="StitchCanvasControl.GestureFrameCount"/>：<b>屏上／出口／判据三方同源</b>，
    /// 这一条与本项目反复守的那句话是同一件事。
    /// </summary>
    private static void DragSegmentCountsOnlyFramesAfterPress()
    {
        using Harness h = Harness.New();
        h.Render();
        h.Render();
        Battery.Assert(!h.Canvas.HasGestureMark,
            $"还没按过鼠标时不该有拖动段，实际 HasGestureMark=true、段帧数 {h.Canvas.GestureFrameCount}"
            + "——起点被谁标了？若不是手势通路标的，那「这一段」就还是全池");
        Battery.Assert(h.Canvas.FrameSampleCount >= 2,
            $"夹具自证：按下之前得先有帧（不然\u201c段比池短\u201d这一句是空话），实际池里 {h.Canvas.FrameSampleCount} 枚");
        Battery.Assert(h.Canvas.DescribeDiagnostics().Contains("拖动段：这一趟还没按过鼠标", StringComparison.Ordinal),
            "没按过时出口必须明写\u201c还没按过\u201d，不许交一个看着像拖动段的数（那是导入期的帧）");

        // 按下＋移动：走的是产品的手势通路（带外 ⇒ 平移；带内 ⇒ 拼缝/对齐拖动，两边都会标起点）
        h.Canvas.MousePress(MouseButtons.Left, Keys.None, new Point(30, 60));
        h.Canvas.MouseMoveTo(new Point(30, 90));
        for (int i = 0; i < 3; i++) h.Render();
        Battery.Assert(h.Canvas.HasGestureMark, "按下并移动之后段起点必须由手势通路标出来");
        Battery.Assert(h.Canvas.GestureFrameCount == 3,
            $"段里应当正好是按下之后画的 3 帧，实际 {h.Canvas.GestureFrameCount}"
            + $"（整池 {h.Canvas.FrameSampleCount} 枚）——对不上就是段没重新起算或退回整池");

        double[] pool = h.Canvas.FrameSamplesMsForTest();
        double[] seg = h.Canvas.GestureFrameSamplesMsForTest();
        Battery.Assert(seg.Length == 3 && pool.Length == seg.Length + (pool.Length - seg.Length),
            $"段样本长度 {seg.Length}，池长度 {pool.Length}");
        for (int i = 0; i < seg.Length; i++)
            Battery.Assert(Math.Abs(seg[i] - pool[pool.Length - seg.Length + i]) < 1e-9,
                $"段里第 {i + 1} 枚必须就是池子末尾数过来第 {i + 1} 枚（段是池的尾巴，不是另取一份）：" +
                $"段 {seg[i]:F3} vs 池 {pool[pool.Length - seg.Length + i]:F3}");
        double tailMax = 0;
        foreach (double ms in seg) tailMax = Math.Max(tailMax, ms);
        Battery.Assert(Math.Abs(h.Canvas.GestureMaxFrameMs - tailMax) < 1e-9,
            $"段最慢必须等于尾巴那几枚的最慢（{tailMax:F3}），实际 {h.Canvas.GestureMaxFrameMs:F3}");
        Battery.Assert(h.Canvas.DescribeDiagnostics().Contains("拖动段（自最近一次按下起）3 帧", StringComparison.Ordinal),
            "出口那一行里的帧数必须就是这个计数器（屏上／出口／判据不许各说一套）");

        // ═══ 第二刀：按下→松手才是"拖动中"，松手之后画的那几帧是"提交重画" ═══
        // 这一刀不是洁癖：真窗口 150% 第一次量"自最近一次按下起"那一段，段最慢 97.6ms
        // 与全池最慢<b>同一枚</b>——那一枚是松手提交触发的重画（一次手势只提交一条可撤销记录）。
        // 如果只切"按下→取数"这一刀，"拖动时最慢 97.6ms"这句就成了假话。
        int dragOnly = h.Canvas.GestureDragFrameCount;
        Battery.Assert(dragOnly == 0,
            $"还没松手时「拖动中」那一段还没封口，应当是 0 帧，实际 {dragOnly}——没封口就报数，"
            + "报的是\u201c按下→取数\u201d那一段，两刀就成了一个名字");
        h.Canvas.MouseRelease(new Point(30, 90));
        Battery.Assert(h.Canvas.GestureDragFrameCount == 3,
            $"松手封口后「拖动中」必须正好是按下之后、松手之前画的那 3 帧，实际 {h.Canvas.GestureDragFrameCount}");
        h.Render();
        h.Render();   // 这两帧是提交重画／后续重绘，不属于"拖动中"
        Battery.Assert(h.Canvas.GestureDragFrameCount == 3,
            "封口之后再画的帧不许回填进「拖动中」那一段（否则提交帧又混回来了）");
        Battery.Assert(h.Canvas.GestureFrameCount == 5,
            $"而整段（按下→取数）应当涨到 5 帧，实际 {h.Canvas.GestureFrameCount}——两刀必须各自是各自的");
        double[] dragSeg = h.Canvas.GestureDragSamplesMsForTest();
        pool = h.Canvas.FrameSamplesMsForTest();
        Battery.Assert(dragSeg.Length == 3, $"拖动中样本长度应当等于封口时那 3 帧，实际 {dragSeg.Length}");
        for (int i = 0; i < dragSeg.Length; i++)
            Battery.Assert(Math.Abs(dragSeg[i] - pool[pool.Length - 5 + i]) < 1e-9,
                $"「拖动中」必须是从按下那一格<b>往后数</b>的第 {i + 1} 帧（尾巴那一种取法会把提交帧圈进来）：" +
                $"拖动中 {dragSeg[i]:F3} vs 池子第 {pool.Length - 5 + i + 1} 枚 {pool[pool.Length - 5 + i]:F3}");
        Battery.Assert(h.Canvas.DescribeDiagnostics().Contains("其中拖动中（按下→松手，不含提交重画）3 帧",
                       StringComparison.Ordinal),
            "出口第二行那 3 帧必须与计数器同源（两行各说一套就等于没切这一刀）");

        // 再按一次：段重新起算成 5 帧（既不是 3+5，也不是整池）
        h.Canvas.MousePress(MouseButtons.Left, Keys.None, new Point(30, 120));
        h.Canvas.MouseMoveTo(new Point(30, 150));
        for (int i = 0; i < 5; i++) h.Render();
        Battery.Assert(h.Canvas.GestureFrameCount == 5,
            $"第二次手势应当把段起点挪到新按下那一刻，段里只留之后的 5 帧，实际 {h.Canvas.GestureFrameCount}"
            + $"（整池 {h.Canvas.FrameSampleCount} 枚）——上一段的帧混进来就是错的段");
        Battery.Evidence($"拖动段：按下前 {pool.Length} 枚在池里、段 3 枚 ⇒ 段确实短于池；第二次手势后段 5 枚");
    }

    /// <summary>
    /// 反证格（内存探针，不动真实源码）：把同一份真样本<b>按两种取法各算一遍</b>——
    /// 整池 vs 段——证明这一格拿到的数据<b>足以</b>把"实现退回整池"这种退化照出来。
    /// 为什么必须单独一格：主判据里那三句（帧数、尾巴相等、最慢等于尾巴最慢）在实现退回整池时
    /// 会同时变成恒真（段=池 ⇒ 长度当然相等、尾巴当然是全部），
    /// 于是"判据被废掉"这件事本身没人管——正是本项目最怕的那一族：静悄悄空转。
    /// 这里显式造那个退化值，并断言它与产品交回的那一支<b>不相等</b>：
    /// 哪天有人把段实现改成直接交回整池，产品交回的那一支就会等于退化那一支，本条立刻红。
    /// </summary>
    private static void WholePoolWouldImpersonateTheDragSegment()
    {
        {
            using Harness h = Harness.New();
            for (int i = 0; i < 4; i++) h.Render();                       // 这 4 枚是"导入期"那一段
            h.Canvas.MousePress(MouseButtons.Left, Keys.None, new Point(30, 60));
            h.Canvas.MouseMoveTo(new Point(30, 90));
            for (int i = 0; i < 2; i++) h.Render();                        // 这 2 枚才是拖动段

            double[] pool = h.Canvas.FrameSamplesMsForTest();
            double[] seg = h.Canvas.GestureFrameSamplesMsForTest();
            double poolMax = 0, segMax = 0;
            foreach (double ms in pool) poolMax = Math.Max(poolMax, ms);
            foreach (double ms in seg) segMax = Math.Max(segMax, ms);

            Battery.Assert(pool.Length > seg.Length,
                $"夹具自证：这份样本里段（{seg.Length} 枚）必须真比整池（{pool.Length} 枚）短，"
                + "否则\u201c退回整池\u201d这种退化在这里根本照不出来，本格就成了白判");
            Battery.Assert(h.Canvas.GestureFrameCount == seg.Length && seg.Length == 2,
                $"产品交回的段帧数 {h.Canvas.GestureFrameCount} 应当就是尾巴那 2 枚");
            Battery.Assert(pool.Length != h.Canvas.GestureFrameCount,
                "把整池长度当成\u201c拖动段帧数\u201d那一种退化实现，必须在这一格被抓到："
                + $"整池 {pool.Length} == 段 {h.Canvas.GestureFrameCount} ⇒ 段已经退回整池了");
            // 尾巴那几枚的最慢一定 ≤ 整池最慢（整池包含尾巴）。这里刻意**不**要求它小于：
            // 拖动段完全可能就是最慢那一段，硬写">"会变成一条看机器心情红的判据。
            Battery.Assert(Math.Abs(h.Canvas.GestureMaxFrameMs - segMax) < 1e-9 && poolMax >= segMax,
                $"段最慢必须自己算尾巴那几枚（段 {segMax:F3}／产品交回 {h.Canvas.GestureMaxFrameMs:F3}／" +
                $"整池 {poolMax:F3}）；整池最慢只能≥段最慢——它包含段，这是「导入帧冒充拖动最慢」那件事的结构");
        }
    }

    // ─────────────── ⓪d 分阶段读数必须把整帧的账加满（需求六"卡顿发生在哪个阶段"） ───────────────

    /// <summary>
    /// 第十四轮量 HUD 开启档那枚 37.4ms 时发现：<b>分阶段只解释得住 20.4ms</b>，
    /// 当时文档写的是"剩下那一截没量出出处"。这一格先把那句话的来源钉掉——
    /// 那一截<b>不是系统抢占，是读数口径的洞</b>：产品只给"取数／棋盘／贴图"三段计了时，
    /// 一帧里另有背景、重影对比层、拼缝线、补丁层、拖动角标、点选引导线这六段从没计过时，
    /// 加上 OnPaint 自己开头那一小截（帧计数、时间戳入队）。三段的话术永远加不出整帧，于是每次都只能猜。
    /// （上一版把这一笔写成"含缓冲交换与派发"是<b>错的</b>：交换与派发发生在 OnPaint 返回之后，
    ///  帧样本的起止都在 OnPaint 里面，天生量不到它们——见本格末尾那句 evidence。）
    ///
    /// 现在结算时补上"读数层"、"叠层"与"帧前"三笔（叠层＝PaintCore 里没逐段计时的绘制，
    /// 帧前＝OnPaint 入口到 PaintCore 之前那一小截），本格判三件事：
    /// ① 六笔之和必须等于这一帧的总时长（漏一笔或重复计一笔都会当场不等）；
    /// ② 每一笔都必须落在 <c>0 ≤ 每笔 ≤ 帧总时长</c> 内（出现负数＝同一笔被算了两遍，
    ///    大于整帧＝它把别段的耗时并进来了）；
    /// ③ 开着补丁层与读数层时"叠层"必须真的>0（写成常量 0 或压根没填，这一条当场红）。
    /// 这一格特意量<b>第二帧</b>（第一帧的取数占大头，其余会被挤得很小，看着像没填）。
    /// 缺口的反证在 <c>guard/分阶段只报前三段时这一帧的账加不满</c>。
    /// </summary>
    private static void StageBreakdownAccountsForTheWholeFrame()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowPatchOverlay = true;       // 这三笔都是"没逐段计时的绘制"，全开让叠层必须显形
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.ShowDebugHud = true;
        h.Render();                              // 第一帧：取数占大头，不作为被判的那一帧
        h.Render();                              // 第二帧（缓存已热）：本格判这一帧
        StitchCanvasControl.StageNumbers n = h.Canvas.LastStageNumbersForTest
            ?? throw new InvalidOperationException("画完一帧必须留下分阶段的数值读数（LastStageNumbersForTest 是空的＝结算没接上）");
        Battery.Assert(n.Frame > 0,
            $"这一帧的总时长读数读到 {n.Frame:F3}ms——必须是正数，否则下面每一笔的比较都是空判");

        double sum = n.Fetch + n.Checker + n.Blit + n.Hud + n.Overlay + n.Prologue;
        Battery.Assert(Math.Abs(sum - n.Frame) < 0.05,
            $"六笔之和必须等于这一帧的总时长（实测 {sum:F3} vs {n.Frame:F3}ms，差 {sum - n.Frame:+0.000;-0.000}）" +
            "——不等就是有一笔被漏掉或重复计：撤掉“叠层／帧前”、把“读数层”算两遍，都会在这里红");

        foreach ((string name, double v) in new (string, double)[]
                 { ("取数", n.Fetch), ("棋盘", n.Checker), ("贴图", n.Blit),
                   ("读数层", n.Hud), ("叠层", n.Overlay), ("帧前", n.Prologue) })
            Battery.Assert(v >= 0 && v <= n.Frame,
                $"{name} 这一笔读到 {v:F3}ms，越界了（要求 0 ≤ 每笔 ≤ 帧总时长 {n.Frame:F3}ms）：" +
                "负数＝同一笔被减了两遍，超过整帧＝它把别的段并进来了");

        // 拆到 PaintCore 的边界为止："叠层"是<em>我们自己</em>没逐段计时的绘制，
        // "帧前"是 OnPaint 一进门到 PaintCore 之前那一小截（帧计数、时间戳入队）。
        // 注意这一格<b>不许</b>把"双缓冲交换与派发"算进任何一笔：那一截发生在 OnPaint 返回之后，
        // 帧样本的起止都在 OnPaint 里，天生量不到它——上一批的措辞写成"帧外＝含缓冲交换与派发"
        // 是把没量过的东西写进口径，本批把它改掉（判据只钉量得到的两笔各判一条）。
        Battery.Assert(n.Overlay > 0.05,
            $"开着补丁层、重影对比层与读数层画一帧，“叠层”只有 {n.Overlay:F3}ms——" +
            "背景、拼缝线、角标这些没逐段计时的绘制总要占一点；读到 0 就是这一笔压根没填或者 PaintCore 的边界取错了");
        Battery.Assert(n.Prologue >= 0 && n.Prologue < n.Frame,
            $"“帧前”（OnPaint 入口到 PaintCore 之前）读到 {n.Prologue:F3}ms，" +
            $"必须在 0..整帧（{n.Frame:F3}ms）之内；它为负说明两次取时钟的次序错了");
        Battery.Evidence($"第二帧 {n.Frame:F2}ms ＝ 取数 {n.Fetch:F2} ＋ 棋盘 {n.Checker:F2} ＋ 贴图 {n.Blit:F2} " +
                         $"＋ 读数层 {n.Hud:F2} ＋ 叠层 {n.Overlay:F2} ＋ 帧前 {n.Prologue:F2}（六笔齐，加得满这一帧；" +
                         "双缓冲交换与派发在 OnPaint 之外，这一套量不到）");
    }

    /// <summary>
    /// 反证（精度红线 §3）。上一格的①只说"五笔加得满"，本格说<b>旧的三段格式加不满</b>——
    /// 也就是这一格新加的那几笔确实有内容，不是把同一笔钱换个名目再报一遍：
    /// 前三段之和必须<b>严格小于</b>整帧，且"读数层 ＋ 叠层 ＋ 帧前"必须正好补上那段缺口。
    /// 若实现退回"只报前三段"（或把叠层写成 0），第一段当场红；
    /// 若把读数层同时算进其余（重复计），第二段当场红。
    /// </summary>
    /// <summary>
    /// 第二十一改：诊断文本里那句算术闸必须<b>两头对得上</b>——
    /// ① 字符串里的行距／行数／面板高三个数各自等于面板的属性读数（两处各写一次，抄错当场红）；
    /// ② 那句"成立／不成立"必须与<b>产品真走的那条路</b>同起同落：说开着，行级计数就 &gt; 0；
    ///   说关着，行级计数就必须是 0。判据读的是行为，不是拿同一个公式再算一遍自证。
    /// </summary>
    private static void DiagnosticTextCarriesTheLineWiseGate()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Render();                                   // 第一帧只能走整块（没有"上一次的行"可比）
        System.Threading.Thread.Sleep(105);           // 越过面板那 100ms 闸门
        h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft + new IntPoint(0, 1));
        h.Render();                                   // 第二帧起才可能走行级

        string[] rows = h.Canvas.DescribeDiagnostics().Split('\n');
        string? gate = null;
        foreach (string row in rows)
        {
            if (row.Contains("行级算术闸")) gate = row.Trim();
        }
        Battery.Assert(gate is not null,
            "复制诊断信息／导出会话那份文本里没有「行级算术闸」这一行——" +
            "150% 那一档的闸成不成立就又只剩工装能算了（那个进程没声明 DPI 感知，算的永远是 96 DPI 那一档）");
        int pitch = h.Canvas.HudPanelLinePitchForTest, count = h.Canvas.HudPanelLineCountForTest;
        (int width, int height) = h.Canvas.HudPanelSizeForTest;
        Battery.Assert(gate!.Contains($"行距 {pitch} × 行数 {count} ＋ 6 ＝ {pitch * count + 6}")
                       && gate.Contains($"对面板高 {height}"),
            $"闸那一行的数与面板的属性读数分家了：文本「{gate}」／属性 行距 {pitch}、行数 {count}、面板高 {height}");

        bool saysOpen = gate.Contains("成立，行级这条路开着");
        long wise = h.Canvas.HudPanelLineWiseRepaints;
        Battery.Assert(saysOpen == (wise > 0),
            $"文本说「{(saysOpen ? "开着" : "关着")}」而行级重画计数是 {wise} 次——这两头必须一起动。" +
            "文本单独成立而一次行级都没走过，说明那句是拼出来的；反之说明闸读的不是它自己那行用的那些数");
        Battery.Evidence($"闸：{gate}；行级 {wise} 次／整块 {h.Canvas.HudPanelWholeRepaints} 次" +
                         $"（96 DPI 这一档现量；150% 那一档由产品这三个出口自己交回同一句）");
    }

    /// <summary>
    /// 反证（第二十一改）：那句话<b>不许是写出来的常数</b>。
    /// 一帧都没画的时候行数 0、面板高 0——这句必须说"不成立"；画过一帧之后它必须变成另一句话。
    /// 没有这一格，前那一格哪怕文本与属性两头一致，也证不出这句话是被<b>量</b>出来的：
    /// 一句写死的"成立"同样能把那几个数抄对。
    /// </summary>
    private static void GateLineWouldLieBeforeTheFirstPaint()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        string before = h.Canvas.DescribeHudPanelGate();
        Battery.Assert(before.Contains("不成立⇒行级这条路关着"),
            $"一帧都没画时这句就该说不成立（那时行数 0、面板高 0），实际「{before}」——" +
            "它在什么都没量的时候就说成立，那它是写出来的，不是闸");
        h.Canvas.ShowDebugHud = true;
        h.Render();
        string after = h.Canvas.DescribeHudPanelGate();
        Battery.Assert(after != before,
            $"画过一帧之后这句一字未变（都是「{before}」）：行数与面板高都还没被量过，它就成了常数");
        Battery.Assert(h.Canvas.HudPanelLineCountForTest > 0 && h.Canvas.HudPanelSizeForTest.Height > 0,
            "夹具自证：画过一帧后面板行数与面板高都必须真有值，否则上面那句\"变了\"只是两次都没量到");
        Battery.Evidence($"画前「{before}」→ 画后「{after}」");
    }

    /// <summary>
    /// 第三十七改（需求四＋需求二）。<b>150% 那一屏「面板装不装得下」工装量不到</b>：
    /// 第十六批那一次读数写着 进程 DPI 96——Checks 宿主进程没有产品那个 DPI 感知声明（第三十六改否证掉的自己的假设）。
    /// 而截屏会拍到聊天内容（隐私红线：屏上内容不进仓库、也不贴给别人），所以这一项不能靠截图交差。
    /// 办法是让<b>产品自己</b>把两个数与判词放进诊断出口（复制诊断信息／导出会话都走同一坨文本）：
    /// 用户在自己的 150% 屏上按一次菜单就把它贴回来，不必判断"有没有被截断"，也没有隐私内容离开这台机器。
    /// 两头各判：文本里的框尺寸必须等于面板属性出口那个数（不许是拼出来的字符串），
    /// 判词必须由那两个数<b>算</b>出来（与 <c>PanelFitsReadouts</c> 同一口径）。
    /// </summary>
    private static void DiagnosticTextCarriesPanelFitLine()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Render();

        string diag = h.Canvas.DescribeDiagnostics();
        string? fit = null;
        foreach (string row in diag.Split('\n'))
            if (row.Contains("面板自证")) fit = row.Trim();
        Battery.Assert(fit is not null,
            "复制诊断信息／导出会话那份文本里没有「面板自证」这一行——150% 那一屏装不装得下就又只能靠截图看，" +
            "而截图带着聊天内容（隐私红线），这一项于是永远停在未验证");

        (int boxW, int boxH) = h.Canvas.HudPanelSizeForTest;
        Size need = StitchCanvasControl.HudTextExtent(h.Canvas.HudTextForTest);
        Battery.Assert(fit!.Contains($"框 {boxW}×{boxH}") && fit.Contains($"{need.Width}×{need.Height}")
                       && fit.Contains($"进程 DPI {h.Canvas.DeviceDpi}"),
            $"这一行的数与面板属性读数分家了：文本「{fit}」／属性 框 {boxW}×{boxH}、真实占位 {need.Width}×{need.Height}、" +
            $"DPI {h.Canvas.DeviceDpi}——三个数有一个不是从出口来的，这句就是写出来的");

        bool fits = boxW >= need.Width && boxH >= need.Height;
        Battery.Assert(fit.Contains("装得下") == (fits && boxH > 0),
            $"文本的判词与那两个数算出来的结果不一致（文本「{fit}」，按数算＝{(fits ? "装得下" : "装不下")}）：" +
            "判词必须是算出来的，否则下一轮它照样绿而屏上的字已经被裁了");
        Battery.Evidence($"{fit}（夹具这一档 96 DPI；用户那台 150% 由这一行自己交回同一口径的数）");
    }

    /// <summary>
    /// 反证（精度红线 §3）。这一句<b>不许是写出来的常数</b>，也不许在什么都没量的时候就说"装得下"：
    /// 面板还没画过时框是 0×0，那句必须说"量不到"。
    /// 没有这一格，前那一格哪怕两头数字一致，也证不出这句话是被量出来的——
    /// 而"0×0 也算装得下"那种写法会让 150% 那一屏的缺陷<b>永远读成已通过</b>（本族第五次以"没量过却说成成立"出现）。
    /// </summary>
    private static void PanelFitLineWouldLieBeforeTheFirstPaint()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        string before = FitLineOf(h.Canvas.DescribeDiagnostics());
        Battery.Assert(before.Contains("量不到") && before.Contains("框 0×0"),
            $"一帧都没画时这句必须说量不到（那时框是 0×0），实际「{before}」——" +
            "拿 0×0 去比谁都小，于是它会永远报装得下，那一屏的截断缺陷就再也读不出来");
        h.Canvas.ShowDebugHud = true;
        h.Render();
        string after = FitLineOf(h.Canvas.DescribeDiagnostics());
        Battery.Assert(after != before && !after.Contains("量不到")
                       && (after.Contains("装得下") || after.Contains("装不下")),
            $"画过一帧之后这句仍是「{after}」（画前「{before}」）：面板已经画过了、框与文本都有值，" +
            "它还停在「量不到」就说明这句是写死的，不是每次算的");
        Battery.Evidence($"画前「{before}」→ 画后「{after}」");
    }

    /// <summary>从那份诊断文本里挑出「面板自证」那一行（找不到回空串，让调用方的断言去红）。</summary>
    private static string FitLineOf(string diag)
    {
        foreach (string row in diag.Split('\n'))
            if (row.Contains("面板自证")) return row.Trim();
        return "";
    }

    private static void ThreeSegmentsAloneWouldUnderAccount()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowPatchOverlay = true;
        h.Canvas.ShowDebugHud = true;
        h.Render();
        h.Render();
        StitchCanvasControl.StageNumbers n = h.Canvas.LastStageNumbersForTest
            ?? throw new InvalidOperationException("画完一帧没留下分阶段数值读数（结算没接上）");
        Battery.Assert(n.Frame > 0,
            "夹具自证：这一帧的总时长读数得 >0，否则下面每一笔的比较都是空判");

        double three = n.Fetch + n.Checker + n.Blit;
        Battery.Assert(three < n.Frame - 0.05,
            $"只报前三段时这一帧的账加不满（前三段 {three:F3}ms、整帧 {n.Frame:F3}ms）——" +
            "这正是旧格式的缺陷；若这一条反而绿了，说明“叠层／帧前”被并进了前三段，重复计数");
        Battery.Assert(Math.Abs(n.Frame - three - (n.Hud + n.Overlay + n.Prologue)) < 0.05,
            "缺的那一段必须正好由“读数层 ＋ 叠层 ＋ 帧前”三笔补上" +
            $"（缺口 {n.Frame - three:F3} vs 补上的 {n.Hud + n.Overlay + n.Prologue:F3}）" +
            "——对不上就是有一笔重复计了前三段已经计过的耗时");
        // 正向对照（第二十二改，#104）：这一笔得跟着"真画了多少没计时的东西"动，不能是个常数。
        // ═══ 为什么把毫秒比较换成调用次数 ═══
        // 上一版比的是<b>各五帧的毫秒平均</b>（开着 → 关着）。同一台机器上它会自己翻面：
        // 第十九改那批第一次读到"开着 1.35ms → 关着 2.00ms"的<b>倒挂 0.65ms</b>，随后两批又各 231／0。
        // 根因不是判据太严，是它判的东西不对——叠层这一笔是<b>减出来的残差</b>，
        // 拿它的毫秒去判"这一帧到底有没有画那些东西"，中间隔着这台机器那一刻忙不忙。
        // 现在读产品自己交的两个调用计数（<c>canvas/最慢帧分阶段要把整帧的账加满</c> 同一批读数）：
        // <b>层开着 ⇒ 那段被调了几次就是几次；层关着 ⇒ 必须是 0 次</b>。毫秒照样交出去，只是退回成读数。
        int patchOn = h.Canvas.OverlayPatchCallsForTest, hudOn = h.Canvas.OverlayHudCallsForTest;
        h.Canvas.ShowPatchOverlay = false;
        h.Canvas.ShowCompareOverlay = false;
        h.Canvas.ShowDebugHud = false;
        h.Render();
        int patchOff = h.Canvas.OverlayPatchCallsForTest, hudOff = h.Canvas.OverlayHudCallsForTest;
        var plain = h.Canvas.LastStageNumbersForTest!;
        Battery.Assert(patchOn > 0 && patchOff == 0,
            $"补丁层的调用次数没跟着开关动（开着 {patchOn} 次 → 关着 {patchOff} 次）：" +
            "开着却是 0 次，说明这段绘制根本没走产品那条路；关着还调，说明那个开关没生效——" +
            "两种情况下「叠层随绘制内容动」这句话都无从判起");
        Battery.Assert(hudOn > 0 && hudOff == 0,
            $"读数层的调用次数没跟着开关动（开着 {hudOn} 次 → 关着 {hudOff} 次）——" +
            "需求三要求 HUD 开／关两档分开报，分开的依据必须是「这一帧到底调没调那一整段」，不是毫秒差");
        Battery.Evidence($"前三段 {three:F2}ms、整帧 {n.Frame:F2}ms、缺口 {n.Frame - three:F2}ms " +
                         $"＝读数层 {n.Hud:F2} ＋ 叠层 {n.Overlay:F2} ＋ 帧前 {n.Prologue:F2}；" +
                         $"关掉三层后末帧的整帧 {plain.Frame:F2}ms（毫秒只作读数）· " +
                         $"闸门读的调用次数：补丁层 {patchOn}→{patchOff} 次、读数层 {hudOn}→{hudOff} 次");
    }

    // ─────────────── ⓪e-2 描边那一笔要拆成边界与命中区两半（第十改） ───────────────

    /// <summary>
    /// 读数层里现在最大那一块是"描边"（真窗口现量：每趟 6.5~8.9ms，整段 HUD 每趟 11~15ms），
    /// 可它里面装的是<b>两种下法完全不同</b>的东西：
    /// ①<b>永远要画</b>的边界两笔（画布边界灰虚线＋视口范围灰实线，都已裁到可见区）；
    /// ②<b>只有选中缝才画</b>的命中区最多四笔（整条带／蓝线上下容差／首尾把手，紫虚线）。
    /// 不劈开就没法决定下一笔降耗动谁：动①会让调试层少一层信息，动②只在选中缝那一档省。
    /// 本格判三件事——两半各自得有数、两半加起来不许超过整笔（否则同一笔钱记了两遍）、
    /// 命中区那半必须与"真画了几笔"同源。
    /// </summary>
    private static void OutlineSplitsIntoBorderAndHitZones()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Canvas.SelectSeam(h.Current.Id);
        h.Render();
        h.Render();
        double outline0 = h.Canvas.HudOutlineMs;
        double border0 = h.Canvas.HudBorderOutlineMs;
        double hits0 = h.Canvas.HudHitsOutlineMs;
        int rects0 = h.Canvas.HudHitRectsDrawn;
        for (int i = 0; i < 5; i++) h.Render();
        double outline = h.Canvas.HudOutlineMs - outline0;
        double border = h.Canvas.HudBorderOutlineMs - border0;
        double hits = h.Canvas.HudHitsOutlineMs - hits0;
        int rects = h.Canvas.HudHitRectsDrawn - rects0;

        Battery.Assert(border > 0 && hits > 0,
            $"选中一条缝连画五帧：边界那半读到 {border:F3}ms、命中区那半读到 {hits:F3}ms——两半都得有数。" +
            "有一边恒 0 就是计时点夹错了位置，那一列是写出来的数，不是量出来的账");
        Battery.Assert(rects >= 5,
            $"五帧只真画了 {rects} 笔命中区矩形：选中缝落在屏上时每帧至少该有一笔" +
            "（笔数比帧数还少，说明这一半的毫秒与\"到底画没画\"脱了钩）");
        Battery.Assert(border + hits <= outline + 0.05,
            $"两半加起来 {border + hits:F3}ms 超过了整笔描边 {outline:F3}ms——两个计时点重叠了，" +
            "同一笔钱记了两遍；那样这一族就不是拆账，而是换个名目再报一遍");
        Battery.Evidence($"五帧：描边 {outline:F2}ms ＝ 边界 {border:F2} ＋ 命中区 {hits:F2} ＋ 残差 {outline - border - hits:F2}" +
                         $"（真画了 {rects} 笔命中区矩形；残差＝取那两个矩形与两次取时钟之间的空隙，它大就是拆得不够）");
    }

    // ─────────────── ⓪f3 两半各自要有自己的计时点（第二十四改） ───────────────

    /// <summary>
    /// 第十一改把"贴图"这一笔劈成两半，可那两半用的是<b>同一对时钟</b>：冷块那一整段
    /// （<c>BitmapOfTile</c> 加 <c>DrawImage</c>）全记进"首次转换"，重采样那半是<b>父项减子项</b>算出来的。
    /// 后果是"一块 512×512 的转换到底值几毫秒"这一问<b>至今没有读数</b>——
    /// 而下一改要决定的正是"转换该不该分摊到几帧"，没有这个数就没资格动它。
    /// 现在两半各取一次时钟，本格判四件事：
    /// ① 冷帧里<b>两半都必须自己有数</b>（旧写法下重采样那半永远是 0，因为压根没测它）；
    /// ② 两半加起来必须<b>贴得住父项</b>（±0.2ms 是取时钟本身的成本；差得远就是漏了一段或多算了一段）；
    /// ③ 全命中那一帧转换必须是 0 块／≈0ms，而<b>重采样必须独自撑起整段</b>——
    ///    这一条才把"重采样只是父项减出来的残差"钉死：<b>残差型的笔不许当闸门</b>（第二十二改那条），
    ///    它现在是一个真计时点，所以在"一帧都没有转换"的时候它必须还报得出父项那么大；
    /// ④ 块数那两列的语义分开：转换列＝真转换的块数（上一格已钉），重采样列＝这一帧贴出去的块数，
    ///    所以冷帧上重采样块数必须<b>不少于</b>转换块数（每块转换过的都要画到屏上）。
    /// 身份仍是<b>子项</b>：六笔加满整帧那条恒等式一字未动
    /// （<c>canvas/最慢帧分阶段要把整帧的账加满</c> 照旧只核六笔），也不参与任何几何或像素决定。
    /// </summary>
    private static void BlitHalvesHaveTheirOwnClocks()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = false;      // 这一格量的是瓦片层，读数层别来搅
        h.Render();
        FillToCached(h);                     // 冷帧可能被瓦片预算截断，先画满再当"冷"的起点
        h.Canvas.ClearTileCacheForTest();    // 造一次「这一屏全部瓦片都要重新上屏」的冷帧
        h.Render();
        var cold = h.Canvas.LastStageNumbersForTest!;
        double convMs = h.Canvas.LastBlitConvertMsForTest;
        double drawMs = h.Canvas.LastBlitDrawMsForTest;
        int convTiles = h.Canvas.LastBlitConvertTilesForTest;
        int drawTiles = h.Canvas.LastBlitDrawTilesForTest;

        Battery.Assert(cold.Blit > 0 && convTiles >= 1,
            $"夹具自证：冷帧贴图父项 {cold.Blit:F3}ms／转换 {convTiles} 块——这一段压根没跑的话，" +
            "下面那三条恒等式全是 0==0 的恒真，这一格就成了守卫空转");
        Battery.Assert(drawMs > 0 && drawTiles >= convTiles,
            $"冷帧只报出重采样 {drawMs:F3}ms／{drawTiles} 块（转换 {convTiles} 块）：每一块转换过的瓦片" +
            "都要画到屏上，重采样那半必须自己有数。它读 0 就说明两半还在共用一对时钟" +
            "（旧写法：整段算进转换、重采样靠父项减出来）——那样「一块转换值几毫秒」仍然没有读数");
        Battery.Assert(convMs <= cold.Blit + 0.05 && drawMs <= cold.Blit + 0.05,
            $"冷帧里首次转换 {convMs:F3}ms 或重采样 {drawMs:F3}ms 超过了父项 {cold.Blit:F3}ms：" +
            "子项比父项还大，说明计时点把取数／棋盘格那一段也算进来了");
        Battery.Assert(Math.Abs(convMs + drawMs - cold.Blit) <= 0.2,
            $"冷帧两半加起来 {convMs + drawMs:F3}ms 与父项 {cold.Blit:F3}ms 差 " +
            $"{Math.Abs(convMs + drawMs - cold.Blit):F3}ms（容差 0.2ms 是取时钟本身的成本）：" +
            "对不上就是有一笔钱既不在转换也不在重采样里，那这一屏的贴图账又合不上了");

        FillToCached(h);                     // 把可能被预算截走的那几块补齐，才谈得上「全命中」
        var warm = h.Canvas.LastStageNumbersForTest!;
        double wConv = h.Canvas.LastBlitConvertMsForTest, wDraw = h.Canvas.LastBlitDrawMsForTest;
        int wConvTiles = h.Canvas.LastBlitConvertTilesForTest, wDrawTiles = h.Canvas.LastBlitDrawTilesForTest;
        Battery.Assert(warm.Blit > 0 && wConvTiles == 0 && wConv < 0.05,
            $"全命中那一帧还报出首次转换 {wConv:F3}ms／{wConvTiles} 块（父项 {warm.Blit:F3}ms）：" +
            "这一帧一块都没转换 ⇒ 转换那半必须 0");
        Battery.Assert(Math.Abs(wDraw - warm.Blit) <= 0.2 && wDrawTiles >= 1,
            $"全命中那一帧重采样只报出 {wDraw:F3}ms／{wDrawTiles} 块，而父项是 {warm.Blit:F3}ms：" +
            "没有转换可减的时候，重采样这一列必须<b>独自</b>撑起整段贴图——" +
            "它撑不起来就说明那半还是父项减出来的残差，不是一个真计时点");
        Battery.Evidence($"冷帧：贴图 {cold.Blit:F2}ms ＝ 首次转换 {convMs:F2}ms／{convTiles} 块 ＋ 重采样 {drawMs:F2}ms／{drawTiles} 块；" +
                         $"全命中帧：贴图 {warm.Blit:F2}ms ＝ 首次转换 {wConv:F2}ms／{wConvTiles} 块 ＋ 重采样 {wDraw:F2}ms／{wDrawTiles} 块" +
                         "（两半各一对时钟；整段的账 blitMs＝convertMs＋drawMs，六笔恒等式因此一字未动）");
    }

    /// <summary>
    /// 反证（精度红线 §3）。要抓的<b>不是</b>"忘了拆"，而是"拆了但两半共用一对时钟"——
    /// 也就是第十一改那一版真实存在过的写法：整段 <c>blitMs</c> 记进转换，重采样＝父项减子项。
    /// <b>先说清哪一条抓得住它</b>：「两半加起来 ≤ 父项」<b>抓不住</b>——那一版里
    /// 转换＝父项、重采样＝0，加起来照样等于父项。真正抓住它的是<b>"重采样那半必须自己有数"</b>
    /// 与<b>"全命中帧上重采样必须独自撑起整段"</b>这两条。把这一点写死在这里，
    /// 是为了下一轮不许拿"恒等式过了"当"拆开了"（本族那条"能被假象满足的判据要写明"）。
    /// 探针是内存里的：拿同一批读数按旧写法重算一遍，不许改真实源文件。
    /// </summary>
    private static void OneClockForBothHalvesWouldLeaveResampleEmpty()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = false;
        h.Render();
        FillToCached(h);
        h.Canvas.ClearTileCacheForTest();
        h.Render();
        var cold = h.Canvas.LastStageNumbersForTest!;
        double convMs = h.Canvas.LastBlitConvertMsForTest, drawMs = h.Canvas.LastBlitDrawMsForTest;

        // 旧写法（同一对时钟）：整段算转换、重采样靠减。
        double oldConv = cold.Blit, oldDraw = cold.Blit - oldConv;
        Battery.Assert(Math.Abs((oldConv + oldDraw) - cold.Blit) <= 0.05,
            "夹具自证：旧写法下「两半加起来＝父项」这条恒等式照样成立——" +
            "所以这条<b>不是</b>抓住旧写法的判据，本格下面用的也不是它");
        Battery.Assert(!(oldDraw > 0),
            "旧写法的重采样那半必须读 0（它就是父项减出来的）。要是这里读到 >0，" +
            "说明正判据第①条已经抓不住这一版了——那第①条得改，不许把这条反证放宽");
        Battery.Assert(drawMs > 0,
            "正向对照：同一帧真读数里重采样必须 &gt;0。旧写法与新写法读出来一样，" +
            "就说明这一列压根没接上计时点，正判据第①条是空转");
        Battery.Evidence($"旧写法（一对时钟）：转换 {oldConv:F2}ms ＋ 重采样 {oldDraw:F2}ms ＝ 父项 {cold.Blit:F2}ms（恒等式照样过）；" +
                         $"新写法：转换 {convMs:F2}ms ＋ 重采样 {drawMs:F2}ms ＝ 父项 {cold.Blit:F2}ms" +
                         " ⇒ 抓住旧写法的是「重采样必须自己有数」，不是那条恒等式");
    }


    /// <summary>
    /// 反证（精度红线 §3）。正判据只要求"两半都 >0"，所以一个<b>把整段描边都算进命中区那一列</b>的实现
    /// 也能全绿——本格就把这一族假绿堵住：取消选中之后，命中区那半的毫秒必须贴着 0、笔数必须一笔不涨，
    /// 而边界那半照旧要有数（它不属于选中态）。掉 0 的只能是该掉的那一半。
    /// </summary>
    private static void HitZoneHalfOnlyMovesWhenHitZonesAreDrawn()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Canvas.SelectSeam(h.Current.Id);
        h.Render();
        h.Render();
        h.Canvas.SelectSeam(null);
        double outline0 = h.Canvas.HudOutlineMs;
        double border0 = h.Canvas.HudBorderOutlineMs;
        double hits0 = h.Canvas.HudHitsOutlineMs;
        int rects0 = h.Canvas.HudHitRectsDrawn;
        for (int i = 0; i < 5; i++) h.Render();
        int rects = h.Canvas.HudHitRectsDrawn - rects0;
        double hits = h.Canvas.HudHitsOutlineMs - hits0;
        double border = h.Canvas.HudBorderOutlineMs - border0;
        double outline = h.Canvas.HudOutlineMs - outline0;

        Battery.Assert(rects == 0,
            $"没选中拼缝却还是画了 {rects} 笔命中区矩形：那一层整层都不该出现在屏上" +
            "（选中态没传到位——调试层会在没选缝时圈出根本不存在的命中区）");
        Battery.Assert(hits < 0.05,
            $"没选中拼缝时命中区那一列读到 {hits:F3}ms：这一列必须只跟着真画出去的那四笔走，" +
            "它照涨说明计时点把边界那两笔也算进来了（正判据会被这种实现骗绿）");
        Battery.Assert(border > 0 && outline >= border,
            $"没选中缝时边界那半读到 {border:F3}ms（整笔 {outline:F3}ms）：边界两笔不属于选中态，" +
            "它掉到 0 就是取消选中把不该省的东西一起省了");
        Battery.Evidence($"取消选中后连画五帧：命中区 {hits:F3}ms／{rects} 笔、边界 {border:F3}ms、整笔 {outline:F3}ms" +
                         "——那一半只跟着真的四笔动");
    }

    // ─────────────── ⓪f 贴图那一笔要拆成首次转换与重采样两半（第十一改） ───────────────

    /// <summary>
    /// HUD 关闭 35% 那一档第 1 趟读到最慢帧 59.4ms，分阶段那一行指着<b>贴图 50.5ms</b>；
    /// 可"贴图"这一笔里混着两件<b>修法完全相反</b>的事：
    /// <list type="number">
    ///   <item><b>首次转换</b>：这块瓦片第一次上屏，整块拷成 GDI 位图（512×512×4 ≈ 1MB，一次性的）
    ///         ——该把它分摊到几帧，也就是瓦片预算那条路；</item>
    ///   <item><b>重采样</b>：每帧必做，静止档用高质量双线性把 512×512 画成屏幕尺寸
    ///         ——该在途中走便宜档、停手那一帧再补回高质量。</item>
    /// </list>
    /// 不劈开就没资格决定下一笔动谁：在量到分界之前，"最慢帧慢在贴图"只算半句归因。
    /// 本格判三件事：① 冷帧那半笔报得出正的毫秒与块数；② 子项不许超过父项（同一笔钱记两遍就是拆错了）；
    /// ③ 缓存焐热之后那半笔要掉到 0，而<b>父项照旧有数</b>——父项也掉 0 就是整段没跑，不算拆账。
    /// 身份与"取数里首次解码"那一笔完全相同：<b>子项</b>，不进六笔恒等式
    /// （<c>canvas/最慢帧分阶段要把整帧的账加满</c> 照旧只核六笔），也不参与任何几何或像素决定。
    /// </summary>
    private static void BlitSplitsIntoConvertAndResample()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = false;      // 这一格量的是瓦片层，读数层别来搅
        h.Render();                          // 先把这一屏的瓦片与位图都焐热
        FillToCached(h);                     // 冷帧可能被 12ms 瓦片预算截断，先画满再当"热"起点
        h.Canvas.ClearTileCacheForTest();    // 造一次「这一屏全部瓦片都要重新上屏」的冷帧
        h.Render();
        var cold = h.Canvas.LastStageNumbersForTest!;
        double coldMs = h.Canvas.LastBlitConvertMsForTest;
        int coldTiles = h.Canvas.LastBlitConvertTilesForTest;

        Battery.Assert(cold.Blit > 0,
            "夹具自证：冷帧的贴图父项读到 0.00ms——这一段压根没跑，" +
            "那么下面「子项 > 0」与「子项 ≤ 父项」两条都成了恒真，这一格就成了守卫空转");
        Battery.Assert(coldTiles >= 1 && coldMs > 0,
            $"清掉瓦片缓存之后那一帧报出首次转换 {coldMs:F3}ms／{coldTiles} 块：这一帧每块瓦片都是" +
            "第一次上屏，两样都必须有数——它是「这半笔真的存在」的唯一证据");
        Battery.Assert(coldMs <= cold.Blit + 0.05,
            $"首次转换 {coldMs:F3}ms 超过了贴图父项 {cold.Blit:F3}ms——子项比父项还大，" +
            "说明计时点把取数或棋盘格那一段也算进来了；那样这不是拆账，是换个名目再报一遍");

        FillToCached(h);                     // 把可能被预算截走的那几块补齐，才谈得上「全命中」
        var warm = h.Canvas.LastStageNumbersForTest!;
        double warmMs = h.Canvas.LastBlitConvertMsForTest;
        int warmTiles = h.Canvas.LastBlitConvertTilesForTest;
        Battery.Assert(warm.Blit > 0,
            "夹具自证：焐热之后贴图父项读到 0.00ms——整段没画，那下一句「子项掉到 0」" +
            "就不是拆开的证据，而是啥都没跑的证据");
        Battery.Assert(warmTiles == 0 && warmMs < 0.05,
            $"缓存全命中那一帧还报出首次转换 {warmMs:F3}ms／{warmTiles} 块：这一帧的贴图全是重采样，" +
            "两块都不该动 ⇒ 计数接在了贴图入口而不是转换分支上");
        Battery.Evidence($"冷帧：贴图 {cold.Blit:F2}ms ＝ 首次转换 {coldMs:F2}ms／{coldTiles} 块（重采样那一半自第二十四改起有自己的计时点，" +
                         "它的读数在下面那一格里核对）；" +
                         $"全命中帧：贴图 {warm.Blit:F2}ms／首次转换 {warmMs:F2}ms／{warmTiles} 块");
    }

    /// <summary>
    /// 反证（精度红线 §3）。正判据只要求"冷帧 >0、热帧 =0"，所以一种<b>假拆账</b>照样能过：
    /// <b>每块画出去的瓦片都记一笔</b>。冷帧它 >0、热帧它 =0（因为那块位图已经在表里），
    /// 两条正判据一条都抓不到——可真窗口滚进半缓存的一屏时，它会把"换了 1 块"报成"换了 9 块"，
    /// 于是下一轮就照着这个数去决定"要不要分摊转换"。
    /// 这一格换一个<b>独立出处</b>对账：<c>canvas.TileBitmapConversionsForTest</c> 是产品里
    /// 只有真转换那一条分支才会涨的进程累计；子项的块数必须与它<b>每一帧的增量一字不差</b>。
    /// 三趟各对一次账：全命中、清缓存后的冷帧、只滚一格（新来的那块要转换、已有的不许算）。
    /// </summary>
    private static void ConvertHalfOnlyMovesWithRealConversions()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = false;
        h.Render();
        FillToCached(h);

        long conv0 = h.Canvas.TileBitmapConversionsForTest;
        h.Render();
        long hotDelta = h.Canvas.TileBitmapConversionsForTest - conv0;
        int hotTally = h.Canvas.LastBlitConvertTilesForTest;
        Battery.AssertEqual(hotDelta, (long)hotTally,
            "全命中那一帧：进程累计转换增量与子项块数必须同为 0。" +
            "子项自己涨起来就是「按画了几块记」而不是「按真转换了几块记」——正判据抓不到这一种假拆账");

        // 冷帧：清掉缓存，两笔必须一起涨、而且数量一致。
        long conv1 = h.Canvas.TileBitmapConversionsForTest;
        h.Canvas.ClearTileCacheForTest();
        h.Render();
        long coldDelta = h.Canvas.TileBitmapConversionsForTest - conv1;
        int coldTally = h.Canvas.LastBlitConvertTilesForTest;
        Battery.Assert(coldDelta > 0,
            $"夹具自证：清掉瓦片缓存后这一帧的转换增量是 {coldDelta}——没得对账，下面那句相等就成了 0==0 的恒真");
        Battery.AssertEqual(coldDelta, (long)coldTally,
            "冷帧：子项块数必须等于这一帧真转换的块数（独立出处是产品自己的进程累计）");

        // 只滚一格：屏上多数块已有位图、新进来那一块才要转换——这一趟最容易被「按块数记」骗。
        h.Canvas.ApplyWheel(-120, System.Windows.Forms.Keys.None, new System.Drawing.Point(200, 250));
        long conv2 = h.Canvas.TileBitmapConversionsForTest;
        h.Render();
        long panDelta = h.Canvas.TileBitmapConversionsForTest - conv2;
        int panTally = h.Canvas.LastBlitConvertTilesForTest;
        Battery.AssertEqual(panDelta, (long)panTally,
            "滚一格那一帧：部分命中的屏上，子项必须只数真转换的那几块。" +
            "把它做成「每块都记」在这一帧就会多报，而下一轮正是拿这个数决定转换要不要分摊");
        Battery.Evidence($"三趟各自对账（进程累计增量＝子项块数）：全命中 {hotDelta}＝{hotTally}、" +
                         $"冷帧 {coldDelta}＝{coldTally}、滚一格 {panDelta}＝{panTally}");
    }

    /// <summary>
    /// 连画几帧直到"这一帧没有一次首次转换"为止。
    /// 为什么要这一步：<c>_tileBudgetMs = 12.0</c> 会在冷活超预算时把剩下的块推到下一帧，
    /// 于是一帧之后还可能是冷帧。不在这里画满，"全命中那一帧"量到的其实是限流中间态。
    /// 上限八帧只是防挂死——真到不了（每帧至少推进一块），到得了就说明限流逻辑坏了，那由别的格子管。
    /// </summary>
    private static void FillToCached(Harness h)
    {
        for (int i = 0; i < 8 && h.Canvas.LastBlitConvertTilesForTest > 0; i++) h.Render();
    }

    // ─────────────── ⓪h 转换那半笔的实现（第十二改：量到 12ms 几乎全是它） ───────────────

    /// <summary>
    /// 转换一次（512×512 那一档）允许的托管分配上限。
    /// <b>两处共用这一个数</b>：正判据拿它量产品（必须远低于），反证拿它量"整块缓冲"那种老写法
    /// （必须超出）。限值写两遍就成了两份各说各话的规则，所以它只有一个出处。
    /// 128KB 是"按行分配"与"按整块分配"之间最宽的一道缝：整块写法一次就是 1MB。
    /// </summary>
    private const int ConversionAllocLimitBytes = 128 * 1024;

    /// <summary>一像素 R、G、B 互不相等的测试瓦片：换序写反一定看得出来，全灰底料看不出来。</summary>
    private static RgbaImage ConversionFixture(int width, int height)
    {
        var img = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                img.SetPixel(x, y, new RgbaColor(
                    (byte)(x * 7 + 1), (byte)(y * 5 + 2), (byte)(200 - x * 3), (byte)(x + y + 3)));
            }
        }
        return img;
    }

    /// <summary>
    /// 独立复算一份 BGRA 字节：<b>不读产品实现、不复用它的换序</b>，按 GDI 内存序的定义直接写。
    /// <paramref name="swapRedBlue"/> 取 false 就是"把 RGBA 原样当 BGRA"那种最常见的写反，反证那一格要用它。
    /// 行按 <paramref name="stride"/> 铺；比较只取每行前 width×4 个字节（GDI 的填充位不属我们的契约）。
    /// </summary>
    private static byte[] RecomputeBgra(RgbaImage image, int stride, bool swapRedBlue)
    {
        int w = image.Width, h = image.Height;
        var dst = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> src = image.Row(y);
            int rowStart = y * stride;
            for (int x = 0; x < w; x++)
            {
                int s = x * 4, d = rowStart + x * 4;
                // RgbaImage 一行是 R,G,B,A；Format32bppArgb 的内存序是 B,G,R,A（小端一像素 0xAABBGGRR）。
                dst[d] = swapRedBlue ? src[s + 2] : src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = swapRedBlue ? src[s] : src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }
        return dst;
    }

    /// <summary>把转换出来的位图按 BGRA 读回内存（走 LockBits，与产品写进去的那条路对称）。</summary>
    private static (byte[] Bytes, int Stride) ReadBackBgra(System.Drawing.Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        System.Drawing.Imaging.BitmapData data = bmp.LockBits(
            rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return (bytes, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>逐行比较（跳过 stride 的填充位），返回第一个对不上的像素序号，全对返回 −1。</summary>
    private static int FirstMismatchedPixel(byte[] actual, byte[] expect, int stride, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            for (int b = 0; b < width * 4; b++)
            {
                if (actual[y * stride + b] != expect[y * stride + b]) return y * width + b / 4;
            }
        }
        return -1;
    }

    /// <summary>
    /// 第十二改把 <c>FileSourceResolver.ToBitmap</c> 的写法换了（逐字节换序 → 逐像素 32 位换序、
    /// 整块临时缓冲 → 按行缓冲），因为第十一改量到真窗口最慢帧的「贴图 12.4／14.1ms」里
    /// <b>首次转换就占 12.4／10.8ms（6～11 块）</b>——那一笔几乎全是转换，不是重采样。
    /// 换写法动的是<b>像素</b>，所以这一格的判据必须是字节而不是耗时：耗时是机器的，
    /// 一像素换错照样跑得飞快。复算写在测试里、按 GDI 内存序的定义直接给，不抄产品实现。
    /// 宽高取 37×23（奇数、且不是 4 的倍数）：整块分配与按行分配这两种写法在这种尺寸上蒙不到彼此。
    /// </summary>
    private static void TileConversionMatchesIndependentRecompute()
    {
        RgbaImage tile = ConversionFixture(37, 23);
        using System.Drawing.Bitmap bmp = FileSourceResolver.ToBitmap(tile);
        Battery.AssertEqual(37, bmp.Width, "转换出来的位图宽度必须与瓦片一致（贴图落点按它算）");
        Battery.AssertEqual(23, bmp.Height, "转换出来的位图高度必须与瓦片一致");
        (byte[] actual, int stride) = ReadBackBgra(bmp);
        int bad = FirstMismatchedPixel(actual, RecomputeBgra(tile, stride, true), stride, 37, 23);
        Battery.Assert(bad < 0,
            $"第 {bad} 号像素起 BGRA 与独立复算对不上：换序（或按行拷贝）写歪了——" +
            "这一笔在显示层，画面上就是红蓝互换，而导出字节不认这个账，所以只有逐字节比对抓得住");
        Battery.Evidence($"37×23 瓦片转换后 851 像素逐字节核对通过（stride={stride}）；" +
                         $"左上角源像素 R={tile.GetPixel(0, 0).R} G={tile.GetPixel(0, 0).G} " +
                         $"B={tile.GetPixel(0, 0).B} A={tile.GetPixel(0, 0).A} → 位图前四字节 " +
                         $"{actual[0]} {actual[1]} {actual[2]} {actual[3]}（B,G,R,A）");
    }

    /// <summary>
    /// 反证（精度红线 §3）。正判据比的是两份字节，所以如果<b>复算本身</b>写反了红蓝，
    /// 两份会一起反、那一格照样绿——这正是"判据能被假象满足"的形状。
    /// 这一格把针摊开：同一份底料造一枚<b>故意不交换红蓝</b>的期望，产品那份必须与它<b>对不上</b>；
    /// 再用同一把尺量<b>正确</b>那份，必须<b>对得上</b>。两头都在，才说明那格判据真的钉在"换序"这一件事上。
    /// </summary>
    private static void ConversionSwizzleReversalIsCaught()
    {
        RgbaImage tile = ConversionFixture(37, 23);
        using System.Drawing.Bitmap bmp = FileSourceResolver.ToBitmap(tile);
        (byte[] actual, int stride) = ReadBackBgra(bmp);

        int wrongBad = FirstMismatchedPixel(actual, RecomputeBgra(tile, stride, false), stride, 37, 23);
        int rightBad = FirstMismatchedPixel(actual, RecomputeBgra(tile, stride, true), stride, 37, 23);
        Battery.Assert(wrongBad >= 0,
            "把红蓝原样透传（最常见的写反）竟然与产品转换逐字节相同：说明底料里 R、G、B 没分开，" +
            "或者这把尺压根没比到那几个字节——那正判据就成了空转");
        Battery.Assert(rightBad < 0,
            $"正确复算反而对不上（第 {rightBad} 号像素）：底料或读回路径变了，" +
            "上面那句「写反要被抓到」在这一刻是恒真的，不算证据");
        Battery.Evidence($"同一把尺两头各量一次：不交换红蓝的期望在第 {wrongBad} 号像素就散；" +
                         $"正确期望整幅一致（37×23）");
    }

    /// <summary>
    /// 转换那半笔的另一头：<b>分配形状</b>。老写法每次调用先 new 一整块 stride×height（512×512 就是 1MB）
    /// 填完再 Marshal.Copy 进位图——一屏十几块就是十几 MB 进大对象堆，而回收的钱会落在
    /// "它发生时正在被计时的那一段"里（这条上一批已经量过：带回收标记的帧把贴图顶到 62.1／68.3ms）。
    /// 这一格不量耗时（耗时是机器的），只量<b>每块瓦片转换的托管分配</b>：必须按行走，远低于限值。
    /// 位图那 1MB 像素在 GDI 非托管堆上，<c>GetAllocatedBytesForCurrentThread</c> 数不到它，
    /// 也不是本轮改的东西——这一笔记的是"我们自己那份临时缓冲"，所以它只能算托管这一侧的账，
    /// 反证那格拿同一把尺量整块写法，保证这限值不是给产品量身定做的宽松数。
    /// 反证在 <c>guard/分配量那把尺必须能量出整块写法</c>。
    /// </summary>
    private static void TileConversionAllocatesPerRowNotWholeBlock()
    {
        RgbaImage tile = ConversionFixture(512, 512);
        const int calls = 8;
        for (int i = 0; i < 2; i++) { using System.Drawing.Bitmap warm = FileSourceResolver.ToBitmap(tile); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        long keep = 0;
        for (int i = 0; i < calls; i++)
        {
            using System.Drawing.Bitmap bmp = FileSourceResolver.ToBitmap(tile);
            keep += bmp.Width * bmp.Height;      // 让编译器不敢把这次调用当死代码删掉
        }
        long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / calls;
        Battery.Assert(perCall < ConversionAllocLimitBytes,
            $"每块 512×512 瓦片转换平均分配 {perCall / 1024}KB，超过限值 " +
            $"{ConversionAllocLimitBytes / 1024}KB：这就是「按整块分配」那一条路——" +
            "一屏十几块十几 MB 进大对象堆，最慢帧的贴图那一笔就是这么来的（本批已经量过：12.4ms／6 块）");
        Battery.Evidence($"转换 512×512 平均每次托管分配 {perCall}B（限值 {ConversionAllocLimitBytes}B，" +
                         $"整块写法是 1MB 起）；用了 {keep} 个像素位，确认调用没被优化掉");
    }

    /// <summary>
    /// 反证（精度红线 §3）。"按行分配"这一侧的限值如果定得比整块分配还宽，上面那格就成了恒绿开关。
    /// 这一格把同一把尺插回<b>老写法</b>：在测试自己的内存里照原样跑一遍
    /// "new 一整块 stride×height → 逐字节换序填 → Marshal.Copy"（只改产品文件以外的那份内存，
    /// 不碰真源码，符合 §3 的反证做法），它必须<b>超出</b>同一个限值；
    /// 并且同一趟里再拿产品的分配数当正向对照——它必须落在限值以内。
    /// </summary>
    private static void AllocationYardCatchesWholeBufferShape()
    {
        RgbaImage tile = ConversionFixture(512, 512);
        const int calls = 4;
        for (int i = 0; i < 2; i++) { ReferenceWholeBufferConversion(tile); }   // 先焐热，别把首调的 JIT 算进账
        long before = GC.GetAllocatedBytesForCurrentThread();
        int sink = 0;
        for (int i = 0; i < calls; i++) sink += ReferenceWholeBufferConversion(tile)[0];
        long wholePerCall = (GC.GetAllocatedBytesForCurrentThread() - before) / calls;

        Battery.Assert(wholePerCall > ConversionAllocLimitBytes,
            $"整块缓冲那条例子这次只分配了 {wholePerCall / 1024}KB，没超过限值 " +
            $"{ConversionAllocLimitBytes / 1024}KB：这把尺量不出「按整块分配」，" +
            "上面那格「必须按行走」因此是恒真的，改回去也不会红");
        Battery.Evidence($"同一把尺两头：整块写法平均每次 {wholePerCall}B（超出限值，" +
                         $"第一个字节的校验和 {sink}），按行写法在 <c>canvas/首次转换的分配量必须按行走而不是整块</c> " +
                         $"那一格里量到的是几百字节级");
    }

    /// <summary>
    /// 【第十四改·归因】读数层（F8 那一整段）往下的账必须<b>按帧</b>记，并且逐帧加起来正好等于
    /// 进程累计在同一批帧上的作差——这条恒等式是这一族第一次把"本帧增量"当成账本主体：
    /// 第十改那批开的出口全是进程累计（工装自己作差报"这一趟"），于是"最慢那一帧里读数层的 33.5ms
    /// 是面板重建、描边，还是贴图"这句话一直没数可答，而这三种钱的下法完全不同。
    /// 本格判三件事：① 开着读数层的每一帧都交回一份拆账（不是只在重建那帧才有）；
    /// ② 各笔加起来不许超过本帧整段（超了就是同一笔钱记两遍，那不叫拆账）；
    /// ③ 逐帧的四个计数加起来<b>必须等于</b>进程累计的作差（少一分是漏账，多一分是把累计抄进本帧）。
    /// <b>耗时不进判据</b>（这一族的规矩），只有"加起来等于/不超过"这种机器无关的账目关系进。
    /// </summary>
    private static void HudStageSplitsPerFrameInsideTheFrameTotal()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Canvas.SelectSeam(h.Current.Id);
        h.Render();
        StitchCanvasControl.HudFrameNumbers? seed = h.Canvas.LastHudNumbersForTest;
        Battery.Assert(seed is not null,
            "开着读数层画了一帧，产品却没交回这一帧的拆账（LastHudNumbersForTest 是空）：" +
            "没有这一份，真窗口那句\"读数层单帧 33.5ms\"就永远只能是一句整段的话");

        long rebuilds0 = h.Canvas.HudPanelRebuilds, blits0 = h.Canvas.HudPanelUnscaledBlits,
             rects0 = h.Canvas.HudHitRectsDrawn, geoms0 = h.Canvas.HudGeometryRebuilds;
        int sumRebuilds = 0, sumBlits = 0, sumRects = 0, sumGeoms = 0;
        double worstOverTotal = 0;
        for (int i = 0; i < 6; i++)
        {
            h.Render();
            StitchCanvasControl.HudFrameNumbers n = h.Canvas.LastHudNumbersForTest
                ?? throw new InvalidOperationException("第 " + (i + 1) + " 帧没交回读数层拆账");
            // 各笔加起来 ≤ 本帧整段。<b>不含几何那一笔</b>：EnsureHudRects 是色块层与读数层共用的缓存，
            // 先跑的那一次在叠层里，它的毫秒落在读数层那两个计时点之外——把它算进来恒等式天生为负。
            double parts = n.Text + n.Border + n.Hits + n.PanelAlloc + n.PanelReuse + n.Blit;
            // 子项不许超过父项：清底＋排字是"面板重画那一路"的两笔（父＝分配＋复用两条路之和）。
            Battery.Assert(n.PanelClear + n.PanelDrawText <= n.PanelAlloc + n.PanelReuse + 0.05,
                $"本帧清底 {n.PanelClear:F3}＋排字 {n.PanelDrawText:F3} 超过了面板重画那一路的父项 "
                + $"{n.PanelAlloc + n.PanelReuse:F3}ms：子项与父项不同源（同一笔钱记了两遍）");
            worstOverTotal = Math.Max(worstOverTotal, parts - n.HudTotal);
            sumRebuilds += n.PanelRebuilds; sumBlits += n.PanelBlits;
            sumRects += n.HitRects; sumGeoms += n.GeometryRebuilds;
        }
        long dRebuilds = h.Canvas.HudPanelRebuilds - rebuilds0,
             dBlits = h.Canvas.HudPanelUnscaledBlits - blits0,
             dRects = h.Canvas.HudHitRectsDrawn - rects0,
             dGeoms = h.Canvas.HudGeometryRebuilds - geoms0;

        Battery.Assert(worstOverTotal <= 0.05,
            $"某一帧里读数层各笔加起来超出整段 {worstOverTotal:F3}ms：两个计时点重叠了，" +
            "同一笔钱记两遍——那一族读数层拆账就不是拆账，而是换个名目再报一遍");
        Battery.Assert(sumBlits == dBlits && dBlits >= 6,
            $"六帧逐帧贴图计数加起来 {sumBlits}，进程累计作差 {dBlits}（六帧至少该有 6 次）：" +
            "对不上就是本帧增量与累计不同源（漏账，或者把累计抄进了本帧）");
        Battery.Assert(sumRebuilds == dRebuilds,
            $"六帧逐帧面板重建加起来 {sumRebuilds}，累计作差 {dRebuilds}——两个数必须相等，" +
            "否则\"这一帧重建了几次\"这句就是推的而不是量的");
        Battery.Assert(sumGeoms == dGeoms && sumRects == dRects,
            $"六帧的几何换算逐帧 {sumGeoms}／累计作差 {dGeoms}，命中区笔数逐帧 {sumRects}／作差 {dRects}：" +
            "这两列也走同一份账，不同源就没法拿它们对账");
        Battery.Evidence($"六帧：贴图 {dBlits} 次、面板重建 {dRebuilds} 次（其中分配那一路在下面的证据行里）、" +
                         $"几何 {dGeoms} 次、命中区矩形 {dRects} 笔；最超整段 {worstOverTotal:F3}ms" +
                         "（各笔加起来 ≤ 整段，残差单列在产品那句文案里）");
    }

    /// <summary>
    /// 反证（精度红线 §3）：第十四改那条恒等式如果被"<b>把进程累计直接抄进本帧</b>"的实现满足，
    /// 六帧加起来就会远大于作差——可那要等六帧之后才红。本格把同一件事在<b>单帧</b>就钉住：
    /// 一帧之内面板<b>最多重建一次</b>（重建点只有一处），而进程累计连着几帧一定 ≥ 1；
    /// 于是"这一帧重建了几次"读数若等于累计（≥2 之后必然 >1）就当众红。
    /// 正向对照：至少有一帧真的重建过一次（全是 0 的账本同样满足"每帧 ≤1"，那是空判据）。
    /// </summary>
    private static void FrameYardDiffersFromProcessCumulativeCounters()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Canvas.SelectSeam(h.Current.Id);
        int maxRebuildsInOneFrame = 0, framesWithRebuild = 0;
        for (int i = 0; i < 8; i++)
        {
            h.Render();
            StitchCanvasControl.HudFrameNumbers n = h.Canvas.LastHudNumbersForTest
                ?? throw new InvalidOperationException("开着读数层却没交回本帧拆账");
            Battery.Assert(n.PanelRebuilds <= 1,
                $"第 {i + 1} 帧读到面板重建 {n.PanelRebuilds} 次：一帧之内重建点只有一处，" +
                "读到 >1 只可能是把<b>进程累计</b>抄进了本帧那一列（正判据的恒等式会被它喂绿一整批）");
            if (n.PanelRebuilds > maxRebuildsInOneFrame) maxRebuildsInOneFrame = n.PanelRebuilds;
            if (n.PanelRebuilds == 1) framesWithRebuild++;
        }
        Battery.Assert(h.Canvas.HudPanelRebuilds >= 1 && framesWithRebuild >= 1,
            $"八帧里一帧都没重建面板（累计 {h.Canvas.HudPanelRebuilds} 次）：那\"每帧 ≤1\"就成了空判据，" +
            "全是 0 的账本满足任何上限——正向对照必须真出现过一次重建");
        Battery.Assert(maxRebuildsInOneFrame == 1,
            $"八帧里单帧最多重建 {maxRebuildsInOneFrame} 次（应当正好有一次）：" +
            "一次都没有就是这一格量的不是重建那一笔，而是别的东西");
        Battery.Evidence($"八帧：单帧最多重建 {maxRebuildsInOneFrame} 次、其中 {framesWithRebuild} 帧重建过、" +
                         $"进程累计 {h.Canvas.HudPanelRebuilds} 次 ⇒ 那把帧尺量的是本帧而不是累计");
    }

    /// <summary>
    /// 【第十五改·降耗】面板复用那条路现在<b>从第一条变了的那行往下重排</b>（第十四改的逐帧拆账量到最慢那帧里
    /// 「面板重画」那一路最大，其中一次排 14 行字占 2.7~3.1ms；而每帧必变的那几行——帧耗时、P50、
    /// 分阶段、Paint 计数——本来就<b>排在最底下</b>，所以往下重排省掉的是上面那十几行）。
    /// 判据<b>不量毫秒</b>（毫秒那档由真窗口的逐帧拆账去读），量的是<b>像素</b>：同一句文字，
    /// 一张按行级增量画出来的缓冲区，与一张按整块重画出来的缓冲区，必须逐字节相同——
    /// 需求三第 5 条省的是「把没变的行再排一遍」，不是省像素，差一个字节就是屏上行为变了。
    /// 夹具自证少一句这格就成了空判据：① 这一帧真走了行级那条路；② 画出去的行数正好等于
    /// 「从第一条变化行到最后一行」（少画＝抗锯齿落点丢了，多画＝钱没省到）；③ 它确实<b>少于</b>总行数。
    /// 本批<b>第一版</b>写的是"只画变了的那几行"＋用 FillRectangle 清行带，这一格当场量出 218832 个字节不同
    /// （半透明底色在 32bppPArgb 上做了一次混合，整张 alpha 全变）——那笔红就是这一格的用法，留着当反面教材。
    /// </summary>
    private static void LineWisePanelRepaintMatchesWholePanelRepaint()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Canvas.SelectSeam(h.Current.Id);
        h.Render();                                       // 第一帧：分配一张新缓冲区＋整块画法
        string[] beforeLines = h.Canvas.HudTextForTest.Split('\n');
        long wiseBase = h.Canvas.HudPanelLineWiseRepaints;
        long paintedBase = h.Canvas.HudPanelLinesRepainted;

        System.Threading.Thread.Sleep(105);               // 越过 10Hz 闸门，让这一帧真的要重画
        h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft + new IntPoint(0, 1));
        h.Render();
        string[] afterLines = h.Canvas.HudTextForTest.Split('\n');

        int changed = 0, firstChanged = -1;
        if (afterLines.Length == beforeLines.Length)
            for (int i = 0; i < afterLines.Length; i++)
                if (afterLines[i] != beforeLines[i])
                {
                    changed++;
                    if (firstChanged < 0) firstChanged = i;
                }
        long wise = h.Canvas.HudPanelLineWiseRepaints - wiseBase;
        long painted = h.Canvas.HudPanelLinesRepainted - paintedBase;

        Battery.Assert(changed > 0 && firstChanged >= 0,
            $"夹具自证：这一帧至少得有一行读数变了（变了 {changed} 行）。一行都没变就不会重画，" +
            "那下面那些断言量的都是「什么都没做」");
        Battery.Assert(afterLines.Length == beforeLines.Length,
            $"行数从 {beforeLines.Length} 变成了 {afterLines.Length}（选中态或行数档位变了）：" +
            "行数变了就名正言顺要整块重排，那种形状由别的格子量，这一格量的必须是不变行数的那种帧");
        Battery.Assert(wise == 1,
            $"这一帧没走行级增量重画（行级计数 +{wise}）：那下面那句「画了 {painted} 行」就只是整块重画了一遍，" +
            "省下来的钱根本不在路上");
        Battery.Assert(painted == afterLines.Length - firstChanged,
            $"第一条变化行是第 {firstChanged} 行（共 {afterLines.Length} 行），从它往下该画 {afterLines.Length - firstChanged} 行，" +
            $"实际画了 {painted} 行：少画＝把没变的那几行的抗锯齿落点丢了，多画＝省的那一半不在路上。" +
            "（<b>不是「只画变了的那 {changed} 行」</b>——本批第一版就是这么写的，判据当场量出 218832 个字节不同）");
        Battery.Assert(painted < afterLines.Length,
            $"{afterLines.Length} 行全画了一遍（画了 {painted} 行）：那行级与整块就没区别了，" +
            "这一格量的挪量得改小到只动底下几行读数（<b>要修的是夹具，不是把判据放宽</b>）");
        // ═══ 第十六改加的落位证据 ═══
        // 这一路从"逐行调用"改成"一条带一次多行调用"之后，"只重排了第一条变化行以下"这件事
        // <b>只剩调用参数这一个证据</b>——`painted` 那枚计数是代码自己按 `lines.Length - first` 加的，
        // 拿它断"少画了几行"是自证（改成一次调用之后它甚至恒等于那个数）。
        // 所以这里断的是<b>交给 GDI 的那一条带的上沿</b>（产品出口，独立于判据里的 firstChanged 算法）：
        // 带顶错了 = 排到隔壁行去了 = 上面那些没变的行被重画、下面那行丢了。
        int pitch = h.Canvas.HudPanelLinePitchForTest;
        Battery.Assert(pitch > 0 && h.Canvas.HudPanelLastRepaintTopForTest == firstChanged * pitch,
            $"第一条变化行是第 {firstChanged} 行、行距 {pitch}，交给 GDI 排版的那一条带上沿该在 " +
            $"{firstChanged * pitch} 像素，实际读到的是一次调用之前的第 {h.Canvas.HudPanelLastRepaintTopForTest} 像素：" +
            "带顶错位＝把上面没变的行重排了（或下面那行丢了），逐字节判据也救不回来");

        using System.Drawing.Bitmap? lineWise = h.Canvas.HudPanelSnapshotForTest();
        h.Canvas.RebuildHudPanelByAllocatingForTest();    // 同一句文字，走<b>整块</b>那条路
        using System.Drawing.Bitmap? whole = h.Canvas.HudPanelSnapshotForTest();
        int diff = CountPanelBytesDiffer(lineWise!, whole!);
        Battery.Assert(diff == 0,
            $"行级增量重画与整块重画比出 {diff} 个字节不同（-1＝两张连尺寸都不一致）：调试层省的那几行" +
            "必须是「一个像素都不许多画也不许少画」，差一个字节就是屏上行为被改了");
        Battery.Evidence($"{afterLines.Length} 行的读数、这一帧真变了 {changed} 行、只画了 {painted} 行，" +
                         $"与整块重画逐字节相同（{diff} 个字节不同）");
    }

    /// <summary>
    /// 反证（精度红线 §3）：上面那格要求「两张缓冲区逐字节相同」，那这把尺必须看得见<b>一行的高低</b>。
    /// 测试内存里把同一句文字按<b>错开 1 像素的行距</b>逐行重画（产品不经过这条路），再与产品整块那张比——
    /// 必须比出不同。这里要是也「相同」，那句「行级与整块相同」就成了恒真的白判据。
    /// </summary>
    private static void LineWiseRepaintYardCatchesOneRowShift()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Canvas.SelectSeam(h.Current.Id);
        h.Render();
        int pitch = h.Canvas.HudPanelLinePitchForTest;
        (int w, int height) = h.Canvas.HudPanelSizeForTest;
        string[] lines = h.Canvas.HudTextForTest.Split('\n');
        Battery.Assert(pitch > 0 && lines.Length * pitch + 6 == height,
            $"夹具自证：面板高 {height} 该等于 {lines.Length} 行 × 行距 {pitch} ＋ 6——产品走行级用的正是这道算术闸，" +
            "对不上就说明这一格量的不是同一张面板（要修的是这里的行距来路，不是把等式改成大约等于）");

        using System.Drawing.Bitmap? whole = h.Canvas.HudPanelSnapshotForTest();
        using var shifted = new System.Drawing.Bitmap(w, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = System.Drawing.Graphics.FromImage(shifted))
        {
            g.Clear(System.Drawing.Color.FromArgb(215, Theme.PanelBackground));
            for (int i = 0; i < lines.Length; i++)
                TextRenderer.DrawText(g, lines[i], Theme.MonoFont,
                    new Rectangle(0, i * pitch + 1, w, pitch), Theme.TextPrimary,
                    StitchCanvasControl.HudPanelFlags);
        }
        int diff = CountPanelBytesDiffer(whole!, shifted);
        Battery.Assert(diff > 0,
            "把每一行往下错 1 像素重画，与产品那张比却一个字节都不差：那「逐字节相同」就是恒真的——" +
            "这把尺看不见行的高低，等于没有判据");
        Battery.Evidence($"错开 1 像素的行级重画与整块那张比出 {diff} 个字节不同 ⇒ 那把尺看得见一行的高低");
    }

    /// <summary>
    /// 第十五改<b>这一格是被真窗口那笔红读数逼出来的</b>：行级重画新开了一条路，它绕开了
    /// <c>PaintHudPanelInto</c>——而第十四改那两笔账（清底／排字）恰恰焊在 <c>PaintHudPanelInto</c> 里。
    /// 于是 5 趟真窗口报的是「复用 7.9ms，其中<b>清底 0.0／排字 0.0</b>」：那一路的钱
    /// 从有名字的桶搬进了没名字的桶，P95 变好变坏都归不到这次改动上。
    /// 第十四改那格的恒等式当时<b>挡不住</b>这一形状，因为它写的是「两笔子项加起来 ≤ 总额」——
    /// 0 + 0 永远满足它。<b>一边的不等式挡不住"尺没接上"</b>，这一格就是把另一头补上：
    /// 走行级那条路的时候，两笔子项<b>必须各自报得出非零</b>，且加起来不许超过复用那一笔（不许虚报）。
    /// 空转对照先量一次（内容没变那一帧两笔都必须不动）：没有它，"非零"可能只是别的帧替它充的账。
    /// 判据：<c>canvas/行级那条重画路必须自己交得出清底与排字两笔</c>，
    /// 反证：<c>guard/两笔面板计时只许跟着真重画涨整块那条路也一样</c>。
    /// </summary>
    private static void LineWiseRoadChargesItsOwnTwoSubItems()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Canvas.SelectSeam(h.Current.Id);
        h.Render();                                       // 第一帧：分配＋整块画法（那一段归反证那格管）

        double idleClear = StitchCanvasControl.HudPanelClearMs;
        double idleDraw = StitchCanvasControl.HudPanelDrawTextMs;
        long idleWise = h.Canvas.HudPanelLineWiseRepaints;
        h.Render();                                       // 没过 10Hz 闸门、文本也没变 ⇒ 本该什么都没做
        Battery.Assert(StitchCanvasControl.HudPanelClearMs == idleClear
                       && StitchCanvasControl.HudPanelDrawTextMs == idleDraw
                       && h.Canvas.HudPanelLineWiseRepaints == idleWise,
            "夹具自证：内容没变的那一帧却把清底／排字或行级次数也涨了——那下面那句「非零」就不是这条路交的账，" +
            "而是别的帧替它充的（这一族叫空转判据）");

        double clear0 = StitchCanvasControl.HudPanelClearMs;
        double draw0 = StitchCanvasControl.HudPanelDrawTextMs;
        double reuse0 = h.Canvas.HudPanelReuseRebuildMs;
        long wise0 = h.Canvas.HudPanelLineWiseRepaints, whole0 = h.Canvas.HudPanelWholeRepaints;
        for (int i = 0; i < 3; i++)
        {
            System.Threading.Thread.Sleep(105);           // 越过 10Hz 闸门
            h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft + new IntPoint(0, 1));
            h.Render();
        }
        long wise = h.Canvas.HudPanelLineWiseRepaints - wise0;
        long whole = h.Canvas.HudPanelWholeRepaints - whole0;
        double clear = StitchCanvasControl.HudPanelClearMs - clear0;
        double draw = StitchCanvasControl.HudPanelDrawTextMs - draw0;
        double reuse = h.Canvas.HudPanelReuseRebuildMs - reuse0;

        Battery.Assert(wise == 3 && whole == 0,
            $"连画三帧，行级那条路该走 3 次、整块那条路该 0 次，实际行级 {wise} 次／整块 {whole} 次：" +
            "整块一旦掺进来，下面那句「两笔非零」可能只证了整块那条路（另一条路照样是白的）");
        Battery.Assert(reuse > 0,
            "夹具自证：这三帧复用那条路的总毫秒读成 0，说明重画压根没发生（或那一笔没接），" +
            "那两笔子项要是不为零反而是别处的钱");
        Battery.Assert(clear > 0 && draw > 0,
            $"复用了 {wise} 次、那一路花掉 {reuse:F1}ms，可拆账里清底报 {clear:F1}ms、排字报 {draw:F1}ms：" +
            "钱从有名字的桶搬进了没名字的桶，第十四改那把尺到这一步就废了（真窗口本批第一版读到的正是这个形状）");
        Battery.Assert(clear + draw <= reuse + 0.05,
            $"两笔子项加起来 {clear + draw:F2}ms 却超过那一路的总额 {reuse:F2}ms：拆账虚报比不拆更糟，" +
            "因为下面的格子会拿它当已归因");
        Battery.Evidence($"行级重画 {wise} 次、那一路共 {reuse:F1}ms，其中清底 {clear:F1}ms／排字 {draw:F1}ms 两笔都接得住");
    }

    /// <summary>
    /// 反证（精度红线 §3）：上面那格拿<b>行级</b>那条路对账，这一格换一条<b>独立的路</b>再量一次同一把尺——
    /// 每次都分配的整块那条画法（<c>PaintHudPanelInto</c>）。为什么要换路：第十五改那个错正是
    /// "尺只焊在一条路上"，若反证还在<b>同一条路</b>上取数，尺焊错了也两头一起错
    /// （本批 §「反证必须换一个独立出处」那条规矩）。
    /// 三段各管一头：① <b>产品真帧里</b>整块那一路（第一帧走的就是它）两笔都得非零；
    /// ② 关掉读数层连画两帧，两笔<b>必须一动不动</b>——没有这一段，"非零"可能是别的工作替它充的账；
    /// ③ 测试后门 <c>RebuildHudPanelByAllocatingForTest</c> 再画一次同一句文字，两笔还得再涨一次。
    /// 这里<b>不比"总额"</b>：那个后门是判据专用的，它照旧分配、照旧计数，却<b>不给自己记那一路的毫秒</b>
    /// （产品里任何一帧都不该走到它，把它的耗时计进"复用／分配"两组里等于让工装替产品做账）。
    /// 本批第一版拿它去比总额，量出"两笔 4.47ms 超过总额 0.00ms"——红的是这一格的尺搭错了出处，
    /// 不是产品少交了账；"两笔不许超过总额"那半边由正判据那格在<b>真帧</b>上管。
    /// </summary>
    private static void WholePanelRoadChargesTheSameTwoSubItems()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = true;
        h.Canvas.SelectSeam(h.Current.Id);
        // 那两笔是<b>进程累计</b>（同一把尺在电池里已经量过几十帧），所以三头全部取增量：
        // 直接断"HudPanelClearMs > 0"在这一格里是恒真的——别的格子早就把它涨上去了。
        double baseClear = StitchCanvasControl.HudPanelClearMs;
        double baseDraw = StitchCanvasControl.HudPanelDrawTextMs;
        h.Render();                                       // 第一帧＝分配一张新缓冲区＋整块画法（产品真帧）

        double clear = StitchCanvasControl.HudPanelClearMs - baseClear;
        double draw = StitchCanvasControl.HudPanelDrawTextMs - baseDraw;
        Battery.Assert(clear > 0 && draw > 0,
            $"画第一帧（整块那条路）本笔增量清底 {clear:F2}ms、排字 {draw:F2}ms：有一条不动就说明" +
            "那两笔计时压根没接在整块那条路上");

        double gateClear = baseClear + clear, gateDraw = baseDraw + draw;
        h.Canvas.ShowDebugHud = false;                    // 关着读数层：面板压根不该碰
        h.Render();
        h.Render();
        Battery.Assert(StitchCanvasControl.HudPanelClearMs == gateClear
                       && StitchCanvasControl.HudPanelDrawTextMs == gateDraw,
            "夹具自证：读数层关着连画两帧，那两笔计时却涨了 ⇒ 涨的不是面板重画，" +
            "上面那句「非零」量的就不是这条路的钱（这一族叫空转判据）");
        h.Canvas.ShowDebugHud = true;

        h.Canvas.RebuildHudPanelByAllocatingForTest();    // 再走一次<b>整块</b>那条画法
        double clearAgain = StitchCanvasControl.HudPanelClearMs - gateClear;
        double drawAgain = StitchCanvasControl.HudPanelDrawTextMs - gateDraw;
        Battery.Assert(clearAgain > 0 && drawAgain > 0,
            $"整块重画同一句文字之后，清底增量 {clearAgain:F2}ms、排字增量 {drawAgain:F2}ms——有一条不动就说明" +
            "两笔计时被搬进了只有行级走得到的地方，那样第十四改与第十五改的拆账就成了两套各说一套的账");
        Battery.Evidence($"同一条尺量两条路：产品真帧里整块那一路交得出清底 {clear:F2}ms／排字 {draw:F2}ms，" +
                         $"关掉读数层那两帧一动不动，行级那一路由正判据那格交账 ⇒ 尺不是只焊在某一条路上的");
    }

    /// <summary>
    /// 本批之前的<b>那一种写法</b>，留在测试里只作内存探针用（产品文件里已经换掉了）：
    /// 一次分配 stride×height 整块，逐字节换序填好，<b>不 LockBits</b>（要的是分配形状与耗时形状，
    /// 不需要真上屏）。返回值当校验和用，防它被优化掉。
    /// </summary>
    private static byte[] ReferenceWholeBufferConversion(RgbaImage image)
    {
        int stride = image.Stride;
        var buffer = new byte[stride * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> src = image.Row(y);
            int rowStart = y * stride;
            for (int x = 0; x < image.Width; x++)
            {
                int s = x * 4, d = rowStart + x * 4;
                buffer[d] = src[s + 2];
                buffer[d + 1] = src[s + 1];
                buffer[d + 2] = src[s];
                buffer[d + 3] = src[s + 3];
            }
        }
        return buffer;
    }

    // ─────────────── ⓪e0 预览那一笔往下拆三段（第二十五改） ───────────────

    /// <summary>
    /// 第二十四改把"贴图"那一笔的两半各接上自己的时钟之后，同一批真窗口读数把下一改的靶子指死了：
    /// 逐趟最慢那一帧里 <b>叠层 12.8~17.4ms，其中"预览"那一列就占 10.1~14.7ms</b>，
    /// 其余五列（背景／拼缝／补丁／角标／点选）加起来只有 1~3ms。
    /// 可"预览"这一列里混着<b>三件不同的事</b>，修法各不相同：
    /// <list type="number">
    ///   <item><b>渲染</b>：把带里那几行按布局取回来（裁切档一次、非裁切档两侧各一次）——贵在该不该复用缓存；</item>
    ///   <item><b>拼对照</b>：把"被裁掉的那两份"按各自落点拼成一张——贵在该不该每帧重拼；</item>
    ///   <item><b>比并贴</b>：差异图或半透明叠层，其中<b>整张 RgbaImage→GDI 位图</b>那一次拷贝
    ///         ——贵在该不该复用那张位图（这一件与"贴图里首次转换"是同一个形状）。</item>
    /// </list>
    /// 不劈开就没资格决定动谁。本格判四件事：① 父列必须真有数（夹具自证）；
    /// ② 渲染与比并贴两段必须各自有数（旧写法下它们全在同一个计时点里）；
    /// ③ 三段加起来不许超过父列，且必须盖住父列的绝大部分（盖不住就是还有一笔没被这三级名字接住）；
    /// ④ <b>比并贴那一段的位图转换次数必须恰好 1 次</b>——重影档与差异档是互斥的两条分支，
    ///    一帧只可能走一条。这一条与机器无关（第二十二改那条：判"有没有做"就数次数，别比毫秒）。
    /// 身份仍是<b>子项</b>：叠层那六列之和与残差的恒等式、六笔加满整帧的恒等式都不受影响。
    /// </summary>
    private static void PreviewSplitsIntoRenderStitchBlit()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowPatchOverlay = true;
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.ShowDebugHud = true;
        h.Render();
        h.Render();
        StitchCanvasControl.OverlayNumbers o = h.Canvas.LastOverlayNumbersForTest
            ?? throw new InvalidOperationException("画完一帧没留下叠层拆账，预览那三段也就无从核对");
        double render = h.Canvas.OverlayPreviewRenderMsForTest;
        double stitch = h.Canvas.OverlayPreviewStitchMsForTest;
        double blit = h.Canvas.OverlayPreviewBlitMsForTest;
        int conversions = h.Canvas.OverlayPreviewConversionsForTest;

        Battery.Assert(o.Preview > 0,
            $"夹具自证：预览那一列读到 {o.Preview:F4}ms——父列本身是 0 的话，" +
            "下面那三条「子项有数」「子项≤父列」全是恒真，这一格就成了守卫空转");
        Battery.Assert(render > 0 && blit > 0,
            $"预览那一列拆出 渲染 {render:F3}／拼对照 {stitch:F3}／比并贴 {blit:F3}ms：" +
            "渲染与比并贴两段每帧都真在跑，任何一段读 0 都说明计时点还共用着（那等于没拆）");
        double subs = render + stitch + blit;
        Battery.Assert(subs <= o.Preview + 0.05,
            $"三段加起来 {subs:F3}ms 超过父列 {o.Preview:F3}ms——同一笔钱被记了两遍，" +
            "那是换个名目再报一遍，不是拆账");
        Battery.Assert(subs >= o.Preview * 0.6,
            $"三段只盖住父列 {o.Preview:F3}ms 的 {subs / Math.Max(o.Preview, 1e-9) * 100:F0}%：" +
            "还剩一大笔没被这三个名字接住，下一改照样不知道该动谁");
        Battery.Assert(conversions == 1,
            $"这一帧对照层做了 {conversions} 次整块位图转换（重影档与差异档互斥，一帧只走一条分支）：" +
            "对不上就是计数接错了地方——它下一轮要用来判「复用有没有生效」，读数错比没读数更贵");
        Battery.Evidence($"这一帧预览 {o.Preview:F2}ms ＝ 渲染 {render:F2} ＋ 拼对照 {stitch:F2} ＋ 比并贴 {blit:F2}ms" +
                         $"（覆盖率 {subs / Math.Max(o.Preview, 1e-9) * 100:F0}%、位图转换 {conversions} 次；" +
                         "叠层那六列与整帧那六笔都没动过）");
    }

    /// <summary>
    /// 反证（精度红线 §3）。正判据第④条钉的是"开着对照层每帧转换 1 次"，
    /// 一种假接法照样能过：<b>把计数接在"这一帧画了叠层"上</b>（而不是接在那两次 ToBitmap 上）——
    /// 关掉对照层之后叠层别的东西还在画，计数就还有数。
    /// 这一格换一个独立出处对账：关掉对照层之后，<b>转换次数必须恰好 0，且预览那一列必须掉到取时钟的噪声以下（&lt;0.05ms）</b>
    /// （预览列是第五改那六列里的一列，不是本格新加的），开着时必须恰好 1 次。
    /// 正向对照在格内：先量开着、再量关着，两趟同一夹具同一帧形状。
    /// </summary>
    private static void PreviewConversionCountTracksTheRealConversion()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowPatchOverlay = true;
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.ShowDifference = false;
        h.Canvas.ShowDebugHud = true;
        h.Render();
        h.Render();
        int on = h.Canvas.OverlayPreviewConversionsForTest;
        double onPreview = h.Canvas.LastOverlayNumbersForTest!.Preview;
        Battery.Assert(on == 1 && onPreview > 0,
            $"夹具自证：开着对照层读到转换 {on} 次／预览列 {onPreview:F3}ms——" +
            "这里就不对的话，下面那句「关掉必须掉 0」是恒真，守卫空转");

        h.Canvas.ShowCompareOverlay = false;
        h.Render();
        int off = h.Canvas.OverlayPreviewConversionsForTest;
        double offPreview = h.Canvas.LastOverlayNumbersForTest!.Preview;
        Battery.Assert(off == 0,
            $"关掉对照层之后转换计数还读到 {off} 次：那是把计数接在了「这一帧画了叠层」上，" +
            "而不是接在那两次 ToBitmap 上——下一轮拿它判「复用有没有生效」就会把没跑的东西算成跑了");
        // 毫秒那一列要给"取时钟本身"留余量（第五改那条反证用的就是 0.05 这道线）：
        // 关着时那一层仍然被调用一次、只是立刻早退，所以它读到的是几微秒，不该判成 0。
        // <b>与机器无关的那条断言（转换次数必须恰好 0）才用等号</b>——这正是第二十二改那条的用法。
        Battery.Assert(offPreview < 0.05,
            $"关掉对照层之后预览列读到 {offPreview:F3}ms（转换 {off} 次）：" +
            "那一列本来就该跟着开关走空（第五改那条反证量过这件事），" +
            "它还有一笔说明这次拆的三段接错了父列");
        Battery.Evidence($"开着：转换 1 次／预览 {onPreview:F2}ms；关掉：转换 0 次／预览 {offPreview:F2}ms" +
                         "（计数与父列一起跟着开关，才说明这三段真在对照层里面）");
    }

    /// <summary>
    /// 第二十六改（正判据）。上一改交出"拼对照 17ms"，可 17ms 里装的三种钱修法完全不同
    /// （行多→该少拼几行／行宽大→每次拷贝都落进大对象堆，该复用缓冲／源图没命中→该看解码，压根不是拼的活）。
    /// 光有毫秒分不出是哪一种，所以这一格要求那一步交出<b>两个与机器无关的数</b>：真取回几行、真分配几字节。
    /// 判三件事：
    /// ① <b>整条带都在屏上时，取回的行数必须恰好等于几何里那两份被裁掉的行数之和</b>
    ///    （t＝后一段被裁的顶部、b＝前一段被裁的底部，两个数都从 <c>SeamGeometry</c> 取——
    ///    这是从几何那本账来的参照，不是把实现里那两条夹取算式抄一遍）；
    /// ② 把带裁掉一半再画一帧：字节数必须落在「对照图那一整块」与「那块 ＋ 取回的那些行按最宽算」之间
    ///    （下界证明它算了 alt 缓冲，上界证明它没把整条带的行数算进来；只有两个数接在<b>同一帧的真分配</b>上才同时成立）；
    /// ③ 夹具自证：开着对照层、走裁切档、带在屏上时行数必须 &gt; 0——否则①②全是恒真，这一格就是守卫空转。
    /// </summary>
    private static void DiscardedCopyReportsItsOwnRowsAndBytes()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowPatchOverlay = true;
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.ShowDifference = false;
        h.Canvas.ShowDebugHud = true;
        Battery.Assert(h.P.Layout.UsesCropPlacement,
            "夹具自证：这条缝不走裁切档，「拼对照」那一步根本不会被调用，下面全是空判");

        CanvasLayout layout = h.P.Layout;
        Seam seam = h.Current;
        SeamGeometry geo = layout.DescribeSeam(seam)
            ?? throw new InvalidOperationException("夹具自证：这条缝算不出几何");
        int t = geo.AppliedAfterCrop, b = geo.AppliedBeforeCrop;
        IntRect band = geo.BandRect;
        Battery.Assert(t + b > 0 && band.Height > 4,
            $"夹具自证：这条缝被裁掉的行数是 {t}＋{b}＝{t + b}、带有 {band.Height} 行——" +
            "取数本来就无行可取时，「行数必须对得上」那一句没有内容");

        // ① 先让整条带都落在屏上（缩放档从 1.0 往小里试，直到 shown 真的等于整带）。
        //    这一步就是自证：三档都装不下就直接红并说明夹具不行，不许拿"没裁到"当通过。
        bool wholeBandVisible = false;
        foreach (double z in new[] { 1.0, 0.5, 0.25 })
        {
            h.Canvas.Viewport.SetZoom(z);
            h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X, band.Y));
            h.Render();
            if (h.Canvas.LastCompareShownForTest == band) { wholeBandVisible = true; break; }
        }
        Battery.Assert(wholeBandVisible,
            $"缩放 1.0／0.5／0.25 三档都没能让整条带（{band.Height} 行）落在可见区里——" +
            "第①条那句「行数恰好等于 t＋b」在这个夹具里量不到，本格必须红着等人换夹具");
        int rows = h.Canvas.OverlayPreviewDiscardedRowsForTest;
        long bytes = h.Canvas.OverlayPreviewDiscardedBytesForTest;
        // 第一趟那两个读数单独留一份：下面还要用同名变量量第二趟，
        // 末尾那句证据必须报<b>被第①条判过的那两个数</b>，不能报被覆盖后的（写结论要连着写它量的是哪一次）。
        int rowsWhole = rows;
        long bytesWhole = bytes;
        Battery.Assert(rows == t + b,
            $"整条带都在屏上时取回 {rows} 行，几何里两份被裁的是 {t}＋{b}＝{t + b} 行：" +
            "行数没接到那两次真取数上（多一行少一行都是把别处的账记进来）");

        // ② 再把带裁掉一半（放大 8 倍，同第八改那一格的拿法）：字节数必须仍夹在两个界之间。
        h.Canvas.Viewport.SetZoom(8.0);
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X, band.Y + 10));
        h.Render();
        IntRect shown = h.Canvas.LastCompareShownForTest;
        rows = h.Canvas.OverlayPreviewDiscardedRowsForTest;
        bytes = h.Canvas.OverlayPreviewDiscardedBytesForTest;
        Battery.Assert(shown.Height > 0 && shown.Height < band.Height,
            $"这一帧没裁到（shown {shown.Height} 行 vs 带 {band.Height} 行）⇒ 第②条量的还是整带那一趟的形状");
        Battery.Assert(rows > 0 && rows < t + b,
            $"裁到可见区之后取回 {rows} 行（整带时是 {t + b} 行）：行数必须跟着可见区走，" +
            "不跟着走就是接在叠层入口按整条带记的");
        long minBytes = (long)shown.Height * shown.Width * 4;
        long maxBytes = minBytes + (long)rows * shown.Width * 4;
        Battery.Assert(bytes >= minBytes && bytes <= maxBytes,
            $"这一步分配了 {bytes} 字节，而「对照图那块＝{minBytes}」与「那块＋取回的 {rows} 行按最宽算＝{maxBytes}」" +
            "是它的两个界：掉到下界以下说明没算 alt 缓冲，超出上界说明把屏外的行也记了进来");
        Battery.Evidence($"整带可见（第①条判的就是这两个数）：取回 {rowsWhole} 行＝几何 t＋b（{t}＋{b}）／分配 {bytesWhole} 字节；" +
                         $"裁到可见区：shown {shown.Height}×{shown.Width}、取回 {rows} 行／分配 {bytes} 字节" +
                         $"（界 [{minBytes}, {maxBytes}]）——下一改该动哪种钱，由这两个数说，不再由毫秒猜");
    }

    /// <summary>
    /// 反证（精度红线 §3）。正判据钉的是"开着对照层、带在屏上时每帧都有行数与字节数"，
    /// 一种假接法照样能过：<b>把这两个数接在叠层入口</b>（"这一帧画了叠层 ⇒ 按整条带记一笔"）。
    /// 这一格换一个独立出处：<b>带整条挪到屏外</b>——同一段代码里那条早退路（第八改那一格量过它，
    /// 它会把 shown 置空并直接 return，拼对照根本没被调用），此时行数与字节数必须恰好 0；
    /// 关掉对照层同样必须 0。正向对照在格内先量一次 &gt; 0，免得"两个数一直是 0"被当成守卫通过。
    /// </summary>
    private static void DiscardedCountsIgnoreFramesWithoutTheStitch()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowPatchOverlay = true;
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.ShowDifference = false;
        h.Canvas.ShowDebugHud = true;
        CanvasLayout layout = h.P.Layout;
        SeamGeometry geo = layout.DescribeSeam(h.Current)!;
        IntRect band = geo.BandRect;

        h.Canvas.Viewport.SetZoom(1.0);
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X, band.Y));
        h.Render();
        int onRows = h.Canvas.OverlayPreviewDiscardedRowsForTest;
        long onBytes = h.Canvas.OverlayPreviewDiscardedBytesForTest;
        Battery.Assert(onRows > 0 && onBytes > 0,
            $"夹具自证：带在屏上时读到 {onRows} 行／{onBytes} 字节——这里就是 0 的话，" +
            "下面那两句「必须掉 0」是恒真，守卫空转");

        // 把带整条挪出可见区（往带下方很远处走，缩放不动，排除"是缩放让它没了"）。
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X, band.Y + band.Height * 40));
        h.Render();
        int offRows = h.Canvas.OverlayPreviewDiscardedRowsForTest;
        long offBytes = h.Canvas.OverlayPreviewDiscardedBytesForTest;
        Battery.Assert(h.Canvas.LastCompareShownForTest.IsEmpty && !h.Canvas.LastCompareBandForTest.IsEmpty,
            "这一帧该是「带算出来了但整条在屏外」：shown 空、band 非空。" +
            "对不上就说明这一帧压根没走到对照层，那句掉 0 就不是被这条早退路证出来的");
        Battery.Assert(offRows == 0 && offBytes == 0,
            $"带移出屏外之后仍读到 {offRows} 行／{offBytes} 字节：那两个数是接在叠层入口" +
            "（或按整条带）记的，不是接在那两次真取数上——下一轮拿它判「复用有没有生效」会把没跑的算成跑了");

        // 换一个独立出处：带回到屏上，但整层关掉。叠层别的东西（补丁、角标）仍在画。
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X, band.Y));
        h.Canvas.ShowCompareOverlay = false;
        h.Render();
        Battery.Assert(h.Canvas.OverlayPreviewDiscardedRowsForTest == 0
                       && h.Canvas.OverlayPreviewDiscardedBytesForTest == 0,
            $"关掉对照层后读到 {h.Canvas.OverlayPreviewDiscardedRowsForTest} 行／" +
            $"{h.Canvas.OverlayPreviewDiscardedBytesForTest} 字节（叠层其余几层这一帧仍在画）：" +
            "计数跟着没被调用的那一步涨，说明它不在那一步里");
        Battery.Evidence($"屏上 {onRows} 行／{onBytes} 字节 → 带移出屏外 0／0 → 整层关掉 0／0" +
                         "（两处独立出处都掉到 0，才说明这两个数真在拼对照那一步里面）");
    }

    /// <summary>
    /// 第二十七改（正判据）。色块层那三笔（两张图的设备矩形框＋重叠带的填充与框）吃的是<b>缓存好的设备矩形</b>，
    /// 看着像"已经算好了"，但拖对齐时重叠带能有好几万行 ⇒ 那条设备矩形也是几万高的一条：
    /// `FillRectangle` 沿整条屏幕外路径铺半透明、虚线框沿整条路径排 dashes。
    /// 真窗口现量（进程累计）里它是色块层最大那一笔：重叠带填充＋描边 127~206ms／趟。
    /// 本格三条都与毫秒无关（批间噪声 25ms，毫秒在这里没有发言权）：
    /// ① 夹具自证——放大 8 倍后带那条设备矩形确实比可见区高（没东西可裁时下面全是恒真）；
    /// ② 产品<b>实际下笔</b>的矩形必须等于判据自己从布局取的两个矩形算出的那一段相交；
    /// ③ 裁完必须真的短了（比整条带矮）——这条防的是"把裁写作把矩形原样传下去"。
    /// </summary>
    private static void ColorLayerDrawsOnlyTheVisiblePart()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.SelectSeam(h.Current.Id);
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.ShowDebugHud = false;      // 色块层是产品默认态：读数层关着它也要画，这一格量的就是它自己
        h.Canvas.Viewport.SetZoom(8.0);
        SeamGeometry geo = h.P.Layout.DescribeSeam(h.Current)
            ?? throw new InvalidOperationException("夹具自证：这条缝算不出几何");
        IntRect bandCanvas = geo.OverlapRect;
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(bandCanvas.X, bandCanvas.Y));
        long framesBefore = h.Canvas.OverlayLayerFrames;
        long clampedBefore = h.Canvas.OverlayOutlinesClamped;
        h.Render();

        Rectangle view = h.Canvas.Viewport.CanvasToDevice(h.Canvas.Viewport.VisibleCanvas());
        Rectangle full = h.Canvas.Viewport.CanvasToDevice(bandCanvas);
        Battery.Assert(full.Height > view.Height + 4,
            $"夹具自证：带那条设备矩形 {full.Height} 高、可见区 {view.Height} 高——" +
            "整条本来就装得下时，「裁过才算数」那一句没有内容");

        Rectangle? expected = StitchCanvasControl.ClipOverlayForTest(view, full, 1f);
        Rectangle drew = h.Canvas.LastOverlayBandDrawForTest;
        Battery.Assert(expected.HasValue && drew == expected.Value,
            $"产品实际下笔的矩形是 {drew}，按可见区独立算出来的是 {expected?.ToString() ?? "null"}：" +
            "两者必须同一个矩形，否则裁与不裁都没人核对（这一格就成了自证）");
        Battery.Assert(drew.Height > 0 && drew.Height < full.Height,
            $"裁完还是 {drew.Height} 高（整条 {full.Height}）⇒ 所谓裁只是把矩形原样传下去，一笔都没省");
        Battery.Assert(h.Canvas.OverlayLayerFrames > framesBefore
                       && h.Canvas.OverlayOutlinesClamped - clampedBefore >= 3,
            $"这一帧进了色块层 {h.Canvas.OverlayLayerFrames - framesBefore} 次、裁过并下笔 " +
            $"{h.Canvas.OverlayOutlinesClamped - clampedBefore} 笔（两张图的框＋重叠带那组＝3）：" +
            "对不上就是计数没接在那三笔真下笔的地方");
        Battery.Evidence($"带整条 {full.Height} 高、可见区 {view.Height} ⇒ 实际下笔 {drew.Height} 高；" +
                         $"这一帧裁过并下笔 {h.Canvas.OverlayOutlinesClamped - clampedBefore} 笔");
    }

    /// <summary>
    /// 反证（精度红线 §3）。正判据钉的是"带在屏上时那一组按裁过的矩形下笔"，
    /// 一种假写法照样能过：<b>把 VisiblePart 去掉、直接画整条缓存矩形</b>——屏上看不出区别
    /// （屏幕外那几万像素本来就看不见），只有这里能抓：带整条挪到屏外时那一组<b>必须一笔都不下</b>。
    /// 空转防护两头都有：正向对照在格内先量一次"带在屏上时下了三笔"；
    /// 而"进了这一层"由 <c>OverlayLayerFrames</c> 单独记（早退那两条路不进），
    /// 免得把"这一帧根本没走到色块层"读成"裁掉了所以没画"。
    /// </summary>
    private static void ColorLayerDrawsNothingWhenBandIsOffScreen()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.SelectSeam(h.Current.Id);
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.ShowDebugHud = false;
        h.Canvas.Viewport.SetZoom(8.0);
        SeamGeometry geo = h.P.Layout.DescribeSeam(h.Current)!;
        IntRect bandCanvas = geo.OverlapRect;

        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(bandCanvas.X, bandCanvas.Y));
        h.Render();
        long framesOn = h.Canvas.OverlayLayerFrames;
        long clampedOn = h.Canvas.OverlayOutlinesClamped;
        Battery.Assert(clampedOn >= 3 && !h.Canvas.LastOverlayBandDrawForTest.IsEmpty,
            $"夹具自证：带在屏上时只读到 {clampedOn} 笔／带那组画的是 {h.Canvas.LastOverlayBandDrawForTest}——" +
            "这里就没下笔的话，下面那句「屏外必须 0 笔」是恒真，守卫空转");

        // 把带整条挪出可见区（往带下方很远处走，缩放不动）：那一组该一笔都不下。
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(bandCanvas.X, bandCanvas.Bottom + 4000));
        h.Render();
        long framesDelta = h.Canvas.OverlayLayerFrames - framesOn;
        long clampedDelta = h.Canvas.OverlayOutlinesClamped - clampedOn;
        Battery.Assert(framesDelta >= 1,
            "这一帧没走到色块层那一步（早退了）：那句掉 0 就不是被裁到可见区证出来的，" +
            "而是被「根本没进这一层」喂绿的");
        Battery.Assert(clampedDelta == 0 && h.Canvas.LastOverlayBandDrawForTest.IsEmpty
                       && !h.Canvas.LastOverlayStrokeDrewForTest,
            $"带整条在屏外时仍下了 {clampedDelta} 笔（带那组画的是 {h.Canvas.LastOverlayBandDrawForTest}，" +
            $"两张图的框至少下一笔＝{h.Canvas.LastOverlayStrokeDrewForTest}）：" +
            "那是把整条几万设备像素高的矩形交给 GDI 沿屏幕外路径铺填充与虚线——屏上看不出区别，只有这条会红");
        Battery.Evidence($"带在屏上：进层＋裁过下笔都在涨；带移出屏外：进层 {framesDelta} 次、下笔 {clampedDelta} 笔" +
                         "（进了这一层但一笔都没下，这才叫裁到可见区）");
    }

    /// <summary>
    /// 第三十改（需求三第 3 条剩下的最后一项：颜色区域使用<b>缓存位图</b>）。
    ///
    /// ═══ 这一格量的是什么 ═══
    /// 前面几批已经把色块层的<b>矩形</b>缓存了、也裁到可见区了，唯独没答过需求三那句
    /// "把这几笔的<b>像素</b>也缓存成一张位图，再一次贴上去"。这条路<b>能实现</b>，所以它可以被量：
    /// 产品走的那条（直接下笔）与另一条（画进一张可见区大小的 32 位 ARGB 层再一次合成）
    /// 共用 <c>DrawColorOps</c> ＋ <c>DrawColorArrows</c> 那一份图元序列（连箭头一起，
    /// 否则差里掺了"少画一层"），两帧之间的<b>全部</b>差别就只剩"分几次合成"。
    ///
    /// ═══ 三件与机器无关的读数，各判一件事 ═══
    /// ① 两条路下的笔数与箭头数必须一样多，整层合成次数必须 0／1——
    ///   防的是"两条分支写成同一份代码"（那才是把判据变成自证）与"缓存那条路少画了一层"。
    /// ② 两条路各自都必须<b>真的改过背景</b>（见下面的空转防护）。
    /// ③ <b>缓存位图那条路与直接画法必须不同</b>（现量：差的像素数与单通道最大偏差都报出来）。
    ///   这条钉的是"这一项在色块层这一族<b>不能</b>靠缓存位图兑现"这个<b>量出来的结论</b>：
    ///   半透明填充先在透明层上合成一次、再被 DrawImage 合成第二次，第二次取整把屏上像素改了。
    ///   它写成"必须 > 0"是有意的：<b>哪天这两条路真的逐像素相同了，这一格会红</b>——
    ///   那时"不许换"的理由消失，才该重开这个决定，而不是让下一轮读到一份过期结论。
    /// 毫秒不作闸门（这一族批间噪声 25ms）；"要不要换"的账单是①③加<b>这条路每帧要新分配的字节数</b>，
    /// 结论必须连着这几个数写进文档。
    ///
    /// ═══ 为什么不拿"产品那一帧"来比 ═══
    /// 第一版把判据借代码画出来的参照帧与<b>产品自己那一帧</b>逐像素比，读到差 319 个像素、
    /// 单通道最大偏差 208。原因不是画错了：管线里色块层<b>之后</b>还有裁边补丁层那一段
    /// （<c>DrawSeams</c> → <c>DrawPatchOverlay</c>），而参照帧是把这几笔加在<b>含补丁层</b>的背景上，
    /// 两处叠放次序相反 ⇒ 叠在一起的那一小片自然不同。
    /// ⇒ 通用一条：<b>比两帧之前要先确认这两帧在被比的那一段之后没有别的绘制</b>；
    /// 这条口径写进证据行，下一轮别再拿产品帧当参照去追那 319 个像素。
    ///
    /// ═══ 空转防护 ═══
    /// 底色是一张关掉了色块层的真帧（条纹页面 ⇒ 逐像素非均匀），色块层没画上去时 ③ 的差会是 0，
    /// 那样"两条路不同"就永远红不起来——本族第三次以"抓法坏了"出现的形状。
    /// 另一头：测完两条路后产品的三本账（进层次数、裁过下笔数、带矩形）必须一动不动，
    /// 否则"量一次对照"就把真窗口的读数顶脏了。
    /// </summary>
    private static void ColorLayerCachedBitmapChangesPixels()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.SelectSeam(h.Current.Id);
        h.Canvas.ShowDebugHud = false;

        // 背景＝关掉重影对比那一层的真帧（色块层一笔都没画的那一屏）
        h.Canvas.ShowCompareOverlay = false;
        using var background = new System.Drawing.Bitmap(400, 500);
        h.Canvas.RenderFrameForTest(background);

        // 产品帧＝同一屏、色块层开着：这一帧由 OnPaint 自己画，同时把设备矩形缓存焐热
        h.Canvas.ShowCompareOverlay = true;
        using var product = new System.Drawing.Bitmap(400, 500);
        h.Canvas.RenderFrameForTest(product);
        Rectangle view = h.Canvas.Viewport.CanvasToDevice(h.Canvas.Viewport.VisibleCanvas());
        long framesBefore = h.Canvas.OverlayLayerFrames;
        long clampedBefore = h.Canvas.OverlayOutlinesClamped;
        Rectangle bandBefore = h.Canvas.LastOverlayBandDrawForTest;

        // 参照帧＝同一份背景＋判据借产品那段代码直接下笔（应当与产品帧一字不差）
        using var direct = new System.Drawing.Bitmap(background);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(direct))
            h.Canvas.DrawColorLayerIntoForTest(g, viaLayer: false);
        int opsDirect = h.Canvas.ColorOpsDrawnForTest;
        int arrowsDirect = h.Canvas.ColorArrowsDrawnForTest;
        int blitsDirect = h.Canvas.ColorLayerBlitsForTest;

        // 候选帧＝同一份背景＋先画进可见区大小的透明层、再一次贴上去
        using var cached = new System.Drawing.Bitmap(background);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(cached))
            h.Canvas.DrawColorLayerIntoForTest(g, viaLayer: true);
        int opsCached = h.Canvas.ColorOpsDrawnForTest;
        int arrowsCached = h.Canvas.ColorArrowsDrawnForTest;
        int blitsCached = h.Canvas.ColorLayerBlitsForTest;

        int directVsBg = CountPanelBytesDiffer(direct, background);
        int productVsBg = CountPanelBytesDiffer(product, background);
        Battery.Assert(opsDirect >= 1 && directVsBg > 0 && productVsBg > 0,
            $"直接画法下了 {opsDirect} 笔、参照帧与背景差 {directVsBg} 处、产品帧与背景差 {productVsBg} 处：" +
            "任何一帧与背景一字不差 ⇒ 那一帧没画色块层，下面那句「产品帧＝参照帧」就成了两边都没画");
        Battery.Assert(opsDirect == opsCached && arrowsDirect == arrowsCached
                       && blitsDirect == 0 && blitsCached == 1,
            $"两条路各下 {opsDirect}／{opsCached} 笔再加 {arrowsDirect}／{arrowsCached} 支箭头，" +
            $"整层合成 {blitsDirect}／{blitsCached} 次：笔数不同就是在比两份不同的图元序列，" +
            "合成次数不是 0/1 就是两条分支写成了同一份代码");
        Battery.Assert(h.Canvas.OverlayLayerFrames == framesBefore
                       && h.Canvas.OverlayOutlinesClamped == clampedBefore
                       && h.Canvas.LastOverlayBandDrawForTest == bandBefore,
            $"这次对照把产品的账顶脏了：进层 {framesBefore} → {h.Canvas.OverlayLayerFrames} 次、" +
            $"裁过下笔 {clampedBefore} → {h.Canvas.OverlayOutlinesClamped} 笔——" +
            "判据借用同一段代码时必须走 counted:false 那条口，否则真窗口读数掺进对照的笔数");

        int productVsReference = DiffArgbPixels(product, direct, out int maxDevOrder);
        int directVsCached = DiffArgbPixels(direct, cached, out int maxDevCached);
        long layerBytes = (long)view.Width * view.Height * 4;
        Battery.Assert(directVsCached > 0,
            $"缓存位图那条路与直接画法现在逐像素相同了（差的像素 0、最大偏差 {maxDevCached}）：" +
            "「这一项不能靠缓存位图兑现」那句是靠现量读出来的，理由一消失就该重开这个决定，" +
            "而不是让文档留着一条过期结论——这一格红了就是提醒去重新量");
        Battery.Evidence($"两条路各下 {opsDirect}＋{arrowsDirect} 笔、整层合成 {blitsDirect}/{blitsCached} 次：" +
                         $"换成缓存位图与直接画法差 {directVsCached} 个像素／单通道最大偏差 {maxDevCached}" +
                         "（这个量级就是「半透明在透明层上合成一次、贴上去再合成第二次」的双取整，肉眼看不出；" +
                         "但判据要的是「同一帧」，不是「差不多的一帧」——精度红线里凡是拿像素当合同的都不接受差不多）；" +
                         $"这条路每帧还要为可见区新分配 {layerBytes / 1048576.0:F2} MB 的层（{view.Width}×{view.Height}）" +
                         "再整层合成一次——像素与分配两头都不划算，需求三第 3 条在色块层这一族到此为止。" +
                         $"另记一笔口径：产品帧与「同一份背景＋直接下笔」的参照帧差 {productVsReference} 个像素" +
                         $"（最大偏差 {maxDevOrder}）——不是画错了，是管线里色块层之后还有裁边补丁层那一段，" +
                         "两条路的叠放次序不同；所以这一格只比两条都由判据借代码画出来的路，不比产品帧");
    }

    /// <summary>
    /// 反证（精度红线 §3）。正判据抓的假写法是"两条路像素相同"由<b>两边都没画</b>喂绿，
    /// 这一格换一个独立出处把这件事钉成一条会红的规矩：<b>把带与两张图整组挪到屏外</b>，
    /// 两条路都必须下 0 笔、且都必须与背景一字不差——
    /// 于是正判据里那句"与背景不同"成了唯一的绿灯来源，谁把 <c>DrawColorOps</c> 的图元序列删空，
    /// 正判据第一条对照就红，而不是安静地报"两条路等价"。
    /// </summary>
    private static void ColorLayerEquivalenceRequiresRealStrokes()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.SelectSeam(h.Current.Id);
        h.Canvas.ShowDebugHud = false;
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.Viewport.SetZoom(8.0);
        SeamGeometry geo = h.P.Layout.DescribeSeam(h.Current)
            ?? throw new InvalidOperationException("夹具自证：这条缝算不出几何");
        // 视口推到带下方很远 ⇒ 三笔该一笔都不下（与第二十七改那枚反证同一个出处，
        // 但那格判的是产品的计数器，这一格判的是<b>像素</b>：屏外时两帧都必须等于背景）
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(geo.OverlapRect.X, geo.OverlapRect.Bottom + 4000));

        using var background = new System.Drawing.Bitmap(400, 500);
        h.Canvas.ShowCompareOverlay = false;
        h.Canvas.RenderFrameForTest(background);
        h.Canvas.ShowCompareOverlay = true;
        using var warm = new System.Drawing.Bitmap(400, 500);
        h.Canvas.RenderFrameForTest(warm);

        using var direct = new System.Drawing.Bitmap(background);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(direct))
            h.Canvas.DrawColorLayerIntoForTest(g, viaLayer: false);
        int opsOffscreen = h.Canvas.ColorOpsDrawnForTest;
        using var cached = new System.Drawing.Bitmap(background);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(cached))
            h.Canvas.DrawColorLayerIntoForTest(g, viaLayer: true);
        Battery.Assert(opsOffscreen == 0,
            $"带整条在屏外时直接画法仍下了 {opsOffscreen} 笔：那一组的裁可见区没生效，" +
            "下面那句「屏外两帧都等于背景」就再也不是由「一笔都没下」得到的");
        Battery.Assert(CountPanelBytesDiffer(direct, background) == 0
                       && CountPanelBytesDiffer(cached, background) == 0
                       && h.Canvas.ColorLayerBlitsForTest == 1,
            $"屏外那两帧与背景分别差 {CountPanelBytesDiffer(direct, background)}／" +
            $"{CountPanelBytesDiffer(cached, background)} 处（缓存那条路合成 {h.Canvas.ColorLayerBlitsForTest} 次）：" +
            "笔数为 0 却把屏改了 ⇒ 这一族的两枚判据都在比别的东西");

        // 正向对照：把视口拉回带上，同一份代码必须立刻下 3 笔并改掉屏上像素
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(geo.OverlapRect.X, geo.OverlapRect.Y));
        h.Canvas.RenderFrameForTest(warm);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(direct))
            h.Canvas.DrawColorLayerIntoForTest(g, viaLayer: false);
        Battery.Assert(h.Canvas.ColorOpsDrawnForTest == 3 && CountPanelBytesDiffer(direct, background) > 0,
            $"拉回带上只读到下笔 {h.Canvas.ColorOpsDrawnForTest} 笔／与背景差 " +
            $"{CountPanelBytesDiffer(direct, background)} 处：这一头不涨，上面那组 0 就是恒真");
        Battery.Evidence($"屏外：下笔 {opsOffscreen} 笔、两帧与背景差 0 处；拉回带上：下笔 " +
                         $"{h.Canvas.ColorOpsDrawnForTest} 笔、与背景差 {CountPanelBytesDiffer(direct, background)} 处");
    }

    /// <summary>
    /// 第三十二改（需求三第 5 条那半句"不要每个 OnPaint 都重新创建 Brush／Font"，
    /// 这一次量的是<b>整帧</b>而不是只有读数层）。
    ///
    /// ═══ 为什么这一格不能只留读数层那一枚 ≤512 字节的门 ═══
    /// 那枚门的口径是"读数层每帧的额外堆分配"，画布自己那一族（底色刷子、补丁虚线笔、
    /// 悬停提示的底与框）从来不在它的范围里——第六改把缝标签那批笔刷提静态之后，
    /// 画布路径上剩下的每帧新建一直没人量过。这一格把出口做进产品：
    /// <c>OnPaint</c> 入口与结算处各取一次 <c>GetAllocatedBytesForCurrentThread</c>。
    ///
    /// ═══ 取数窗口是这一改自己踩过的坑（第一版问错了问题）═══
    /// 第一版没有产品侧出口，判据就在电池里连画 20 帧量总分配，读到 <b>91058B／帧</b>，
    /// 看着像画布每帧撒了 90KB。实际那笔账里最大的一块是<b>工装路径自己</b>：
    /// <c>RenderFrameForTest</c> 每帧 <c>Graphics.FromImage</c> ＋ <c>new PaintEventArgs</c>，
    /// 都在 OnPaint 之外。⇒ 与"双缓冲交换与派发在 OnPaint 之外、这一套帧样本量不到"同源的一条：
    /// <b>按帧量的东西必须由产品自己在帧内取数</b>；判据替产品取数，就把工装的开销记到了产品账上。
    /// 修法是把出口做进产品（这一格读的就是它），<b>不是</b>把上限放宽到能容下那 90KB。
    ///
    /// ═══ 这道线能挡什么、挡不住什么（都是量出来的，不是声明的）═══
    /// 现量：稳态帧 <b>88680B／帧</b>（HUD 关）。上限取 <see cref="WarmFrameAllocCeilingBytes"/>，
    /// 挡得住的是"每帧多拷一块像素缓冲"这一族数量级的回归（对照层每帧的分配正是这一档，见第二十六改）。
    /// <b>挡不住的是它本来想挡的那一族</b>：把每帧新建的底色刷子与补丁虚线笔<b>原样复现</b>一次，
    /// 只有 <b>224B／帧</b>（4 支对象），现量的 0.25%——字节尺在它上面没有分辨率。
    /// 所以那一族改用两把尺：产品侧把它们提到静态（本改的改动），加上 <c>round14_audit.py</c>
    /// 把"每帧新建那两支"的字面钉进 <c>MUST_BE_ABSENT</c>（写回去就直接红，不靠毫秒也不靠字节）。
    /// 位图那 1MB 在 GDI 非托管堆上，这把尺压根数不到，那笔账归贴图预算那两枚门。
    /// </summary>
    private const long WarmFrameAllocCeilingBytes = 131072;

    private static void WarmCanvasFrameHasBoundedHeapAllocation()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = false;       // 这一格量画布本身；读数层那一族早有 ≤512 字节那枚门管着
        h.Render();
        h.Render();
        h.Render();
        Battery.Assert(h.Canvas.LastBlitConvertTilesForTest == 0,
            "稳态帧自证：这一帧还有首次转换 ⇒ MB 级的冷活会盖掉画布自己那几支笔刷的账，" +
            "这一格量的就不是每帧新建 GDI 对象这件事了");
        Battery.Assert(h.Canvas.FrameAllocSamplesForTest == 3 && h.Canvas.PaintCount == 3,
            $"画了 {h.Canvas.PaintCount} 帧、出口只结算 {h.Canvas.FrameAllocSamplesForTest} 帧：" +
            "读数没跟着每帧走（漏一帧就是拿一个不动的数自证），下面那句『≤ 上限』就成了恒真");
        long bytes = h.Canvas.LastFrameAllocBytesForTest;
        Battery.Assert(bytes <= WarmFrameAllocCeilingBytes,
            $"稳态帧每帧托管堆分配 {bytes}B 超过上限 {WarmFrameAllocCeilingBytes}B：" +
            "画布路径上每一支每帧新建的笔／刷都记在这笔账里（需求三第 5 条），" +
            "颜色与线宽固定的那些必须提到静态");
        Battery.Evidence($"稳态帧每帧托管堆分配 {bytes}B（上限 {WarmFrameAllocCeilingBytes}B，只挡数量级回归：" +
                         "每帧新建那 4 支固定颜色的笔刷现量只有 224B／帧，这把尺在它上面没有分辨率）；" +
                         $"三帧累计 {h.Canvas.FrameAllocBytesTotalForTest}B／结算 {h.Canvas.FrameAllocSamplesForTest} 帧");
    }

    /// <summary>
    /// 反证（精度红线 §3）。这把新尺要真能量到东西，得先证明它<b>看得见一帧里的额外活</b>：
    /// 同一块画布、同一个视口，只把 F8 读数层打开，那一帧的分配必须<b>大于</b>关着的那一帧。
    /// 两帧一样就说明出口接错了地方（或压根没结算），上一格那句"≤ 上限"于是恒真。
    /// 另一头钉接线：样本数必须跟着 Paint 次数一格不差地走——漏结算与重复结算都会让"按帧归一"变成假的。
    /// </summary>
    private static void FrameAllocationReadingSeesExtraWorkPerFrame()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = false;
        h.Render();
        h.Render();
        h.Render();
        long offBytes = h.Canvas.LastFrameAllocBytesForTest;
        long samplesOff = h.Canvas.FrameAllocSamplesForTest;
        long paintsOff = h.Canvas.PaintCount;
        h.Canvas.ShowDebugHud = true;
        h.Render();
        long hudBytes = h.Canvas.LastFrameAllocBytesForTest;
        Battery.Assert(samplesOff >= 3 && h.Canvas.FrameAllocSamplesForTest == samplesOff + 1
                       && h.Canvas.PaintCount == paintsOff + 1,
            $"画了 {h.Canvas.PaintCount - paintsOff} 帧、样本却从 {samplesOff} 涨到 " +
            $"{h.Canvas.FrameAllocSamplesForTest}：出口没接在每帧结算上（漏一帧或多算一帧，按帧归一就成了假的）");
        Battery.Assert(hudBytes > offBytes,
            $"开着读数层那一帧分配 {hudBytes}B、关着 {offBytes}B：开着不比关着大 ⇒ 这把尺看不见一帧里的额外活，" +
            "上一格那句 ≤ 上限就是恒真——它抓不到任何每帧新建的东西");
        Battery.Evidence($"同一块画布：关读数层 {offBytes}B／帧、开读数层 {hudBytes}B／帧" +
                         $"（多 {hudBytes - offBytes}B，尺看得见）；样本与 Paint 同步 +1");
    }

    /// <summary>
    /// 第三十三改（归因，不是降耗）。上一改刚开的那枚<b>整帧</b>分配读数在真窗口读到 6.4~8.3 MB／帧，
    /// 而电池稳态帧只有 88 KB／帧——差 70 多倍。同一命令行另一行写着「每帧重算 6~8 块瓦片」，
    /// 把这两个数连起来只需要一句话：一块冷瓦片的像素缓冲。<b>这句话要是错的，下一改就动错地方</b>
    /// （取数段的修法是收窄作废范围／复用缓冲，转换段的修法是分摊到几帧，第十二改已把它改成按行走）。
    /// 所以这一格不判快慢，只要求产品把三段各自交出来：恒等式取
    /// 「三段加起来不许超过整帧那一笔」——把某一段写成"整帧减其余"就是拿定义当结论（精度红线 §3 那一族老坑）。
    /// </summary>
    private static void FrameAllocationSplitsIntoFetchAndConvert()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowDebugHud = false;      // 这一格量取数与转换两段；读数层那一族的开关归反证那格钉
        h.Render();
        long totalCold = h.Canvas.LastFrameAllocBytesForTest;
        long fetchCold = h.Canvas.LastFrameAllocFetchBytesForTest;
        long convCold = h.Canvas.LastFrameAllocConvertBytesForTest;
        int coldTiles = h.Canvas.LastBlitConvertTilesForTest;
        Battery.Assert(coldTiles >= 1 && fetchCold > 0,
            $"冷帧：冷块 {coldTiles} 块、取数段 {fetchCold}B——冷渲染一块瓦片却不记一笔字节，" +
            "说明那只表没接在真取数那一步（接错段就会把 MB 级那一笔记到别头上，下一改动错地方）");
        Battery.Assert(fetchCold + convCold <= totalCold,
            $"取数 {fetchCold}B ＋ 转换 {convCold}B ＝ {fetchCold + convCold}B 超过整帧那一笔 {totalCold}B：" +
            "分段必须各自量自己那对边界，超过只能是有一口表取错了位置");

        // ═══ 第三十四改：再交出第四段（对照层整段）═══
        // "其余"那一档在真窗口稳定 1.3MB／帧，而第二十六改早有「拼对照那一步取回几行／几字节」——
        // 两把尺不是一把（那把数子步取回的行缓冲，这把数整段分配），所以本段把对照层整段的份额交出来。
        h.Canvas.SelectSeam(h.Current.Id);
        h.Canvas.ShowCompareOverlay = true;
        h.Render();
        long totalPv = h.Canvas.LastFrameAllocBytesForTest;
        long fetchPv = h.Canvas.LastFrameAllocFetchBytesForTest;
        long convPv = h.Canvas.LastFrameAllocConvertBytesForTest;
        long pvPv = h.Canvas.LastFrameAllocOverlayPreviewBytesForTest;
        Battery.Assert(pvPv > 0,
            $"开着对照层渲染一帧，对照层那一段读到 {pvPv}B：这一段压根没被量到，" +
            "那「其余」那一档就永远是猜的（第二十六改的子步读数与这把整段尺也对不上）");
        Battery.Assert(fetchPv + convPv + pvPv <= totalPv,
            $"取数 {fetchPv}B ＋ 转换 {convPv}B ＋ 对照层 {pvPv}B ＝ {fetchPv + convPv + pvPv}B " +
            $"超过整帧 {totalPv}B：四段必须各自夹住自己那对边界，超过只能是有一口表取错了位置");

        // 稳态帧（全命中）：转换段必须归 0、取数段必须比冷帧小一个数量级以上，而整帧仍要有数——
        // 这一条把「分段其实是整帧减出来的」那种写法判死（减出来的段在这种帧上会跟着整帧一样大）。
        h.Render();
        long totalWarm = h.Canvas.LastFrameAllocBytesForTest;
        long fetchWarm = h.Canvas.LastFrameAllocFetchBytesForTest;
        long convWarm = h.Canvas.LastFrameAllocConvertBytesForTest;
        Battery.Assert(h.Canvas.LastBlitConvertTilesForTest == 0,
            "第二帧仍有冷块 ⇒ 下面那句「命中帧两段归零」这一格压根没量到（夹具不自证就等于恒真）");
        Battery.Assert(convWarm == 0 && fetchWarm * 8 < fetchCold && totalWarm > 0,
            $"全命中的稳态帧：转换段 {convWarm}B、取数段 {fetchWarm}B（冷帧取数段 {fetchCold}B）、整帧 {totalWarm}B——" +
            "转换段只在冷块上做工，命中帧必须归 0；取数段允许一个常数级的小分配（现量 96B，那是缓存命中路径上的），" +
            "但必须比冷帧小一个数量级以上，否则这一段量的就不是取数");
        Battery.Evidence($"冷帧：整帧 {totalCold}B＝取数 {fetchCold}B＋转换 {convCold}B＋其余 {totalCold - fetchCold - convCold}B（冷块 {coldTiles} 块）；" +
                         $"开对照层那一帧：整帧 {totalPv}B＝取数 {fetchPv}B＋转换 {convPv}B＋对照层 {pvPv}B＋其余 " +
                         $"{totalPv - fetchPv - convPv - pvPv}B；" +
                         $"稳态帧：整帧 {totalWarm}B／取数 {fetchWarm}B／转换 {convWarm}B。" +
                         "对照层那一格与「其余」还剩多少，就是真窗口 1.3MB／帧那档的归属读数");
    }

    /// <summary>
    /// 反证（精度红线 §3）。三只新表要有两处<b>独立</b>的"没做这件事"出处，否则任意一只接反了都照样绿：
    /// ① 读数层开关——关着必须恒 0、开着必须 &gt; 0（两头都在格内，正向对照不许外包给下一轮）；
    /// ② 视口挪出画布——一块瓦片都不取，取数与转换两段必须同时归 0，而整帧仍要有数。
    /// ②专门抓"某一段其实是整帧减出来的"那种写法：减出来的段在这种帧上会跟着整帧一起涨。
    /// </summary>
    private static void AllocStageReadingsFollowRealWorkOnly()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();

        h.Canvas.ShowDebugHud = false;
        h.Render();
        long hudOff = h.Canvas.LastFrameAllocHudBytesForTest;
        h.Canvas.ShowDebugHud = true;
        h.Render();
        long hudOn = h.Canvas.LastFrameAllocHudBytesForTest;
        Battery.Assert(hudOff == 0 && hudOn > 0,
            $"读数层那一段：关着 {hudOff}B、开着 {hudOn}B。关着也涨 ⇒ 把画布的钱记给了读数层；" +
            "开着不涨 ⇒ 这一族永远没有读数（空转门）");

        // ═══ 出处二（第三十四改新增）：对照层整层关掉 ═══
        // 这一档专防"整层不画了但那一段的表还在涨"（把别处的钱记给对照层）。
        // 两头各判一次：开着必须有数、关掉必须<b>小一个数量级以上</b>，且关掉那一帧整帧仍要有数。
        // 顺序也讲究：这一档必须在视口挪走<b>之前</b>跑——带要在屏上，否则"开着有数"量的就不是真画对照层。
        h.Canvas.SelectSeam(h.Current.Id);
        h.Canvas.ShowCompareOverlay = true;
        h.Render();
        long pvOn = h.Canvas.LastFrameAllocOverlayPreviewBytesForTest;
        long totalOn = h.Canvas.LastFrameAllocBytesForTest;
        h.Canvas.ShowCompareOverlay = false;
        h.Canvas.ShowDifference = false;
        h.Render();
        long pvOff = h.Canvas.LastFrameAllocOverlayPreviewBytesForTest;
        long totalOff = h.Canvas.LastFrameAllocBytesForTest;
        Battery.Assert(pvOn > 0,
            $"开着对照层那一帧，整层那一段读到 {pvOn}B：正向对照先立不住，下面那句关掉必须小就是恒真");
        // 关掉整层不等于这一段一个字节都不花：入口那一步（选中缝＋布局里找两段）本身要分配（现量 104B）。
        // 所以判据钉的是"跟着真画对照层涨"，不是"归 0"——后者是上一批被现量否证过的那类判据假设。
        Battery.Assert(pvOff * 8 < pvOn && totalOff > 0,
            $"整层关掉之后对照层那一段还记 {pvOff}B（开着 {pvOn}B、关掉那帧整帧 {totalOff}B）：" +
            "这一段跟着的不是真画对照层这件事（差不到一个数量级就是表接错了位置）");

        // 出处三：视口整段挪到画布之外（一块瓦片都不取）。HUD 关掉，免得读数层那一笔混进来。
        h.Canvas.ShowDebugHud = false;
        h.Render();
        long baseTotal = h.Canvas.LastFrameAllocBytesForTest;
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(0, h.P.Layout.CanvasSize.Height + 2000));
        h.Render();
        long fetchOut = h.Canvas.LastFrameAllocFetchBytesForTest;
        long convOut = h.Canvas.LastFrameAllocConvertBytesForTest;
        long totalOut = h.Canvas.LastFrameAllocBytesForTest;
        Battery.Assert(h.Canvas.LastBlitDrawTilesForTest == 0,
            "视口挪出画布之后仍在贴瓦片 ⇒ 这一帧不是「没取数」的形状，下面那句归 0 就没被量过");
        Battery.Assert(fetchOut == 0 && convOut == 0,
            $"屏上没一块瓦片时取数段 {fetchOut}B、转换段 {convOut}B（整帧 {totalOut}B）：" +
            "只要有段跟着非零，就是那一段其实是拿整帧减出来的（定义冒充结论那一族）");
        Battery.Assert(totalOut > 0 && totalOut <= baseTotal * 4,
            $"空帧整帧分配 {totalOut}B（稳态帧 {baseTotal}B）——整帧归 0 说明出口没结算，" +
            "涨过 4 倍说明挪视口这件事本身在造 MB 级分配，两种都要先修读数再接下一改");
        Battery.Evidence($"读数层：关 {hudOff}B／开 {hudOn}B；对照层：开 {pvOn}B（整帧 {totalOn}B）／关 {pvOff}B（整帧 {totalOff}B）；" +
                         $"视口在画布外：取数 {fetchOut}B／转换 {convOut}B／整帧 {totalOut}B（稳态整帧 {baseTotal}B）");
    }


    /// <summary>
    /// 第三十一改（需求三第 5 条那半句"不要每个 OnPaint 都重新创建 Brush"，加上一笔屏上看不出的纯搬运）。
    ///
    /// ═══ 病因 ═══
    /// 贴图循环里每块瓦片都先铺一遍棋盘格再盖位图：一块 512×512 要填 <b>4096 个 8×8 方格</b>，
    /// 而真实截图的瓦片 alpha 恒为 255——铺完立刻被同样不透明的那张位图整个盖住。
    /// 真窗口现量：棋盘这一笔 1.2~2.9ms／帧，是需求五那一档里唯一"看不出、但每帧都在做"的一段。
    /// 顺带这里<b>每次铺都新建两支 SolidBrush</b>，正是需求三第 5 条点名的形状。
    ///
    /// ═══ 判据为什么不能只报"棋盘 0 格" ═══
    /// 如果这些瓦片本来就带透明，"0 格"是<b>漏画</b>而不是省电。所以这一格先<b>自己独立数一遍</b>：
    /// 从产品的位图缓存里取出这一帧真正上屏的那几块瓦片，逐块调 Core 的 <c>IsFullyOpaque()</c>
    /// （不是读产品那张判定表——读了就是拿结论证结论）。前提成立，产品的"0 格"才有内容。
    /// 两头还各钉一枚计数：判定扫描次数在一帧之后不许再涨（判定表真的复用），
    /// 而棋盘格方格数必须与"贴了几块"脱钩（贴了 N 块、铺 0 格）。
    /// </summary>
    private static void CheckerOnlyDrawsWhereTransparencyShows()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();

        // 第一帧：冷块（要转换）。之后连画几帧，直到出现一帧<b>一块冷块都没有</b>——
        // 复用这一半只有在那种帧上才判得出来（拿总数比缓存块数比不出这件事：
        // 中间任何一块被重渲染都会既多一次转换又多一次扫描，那是应该的）。
        h.Render();
        long scansFrame1 = h.Canvas.TileOpacityScans;
        int tilesFrame1 = h.Canvas.LastBlitDrawTilesForTest, convertsFrame1 = h.Canvas.LastBlitConvertTilesForTest;
        int squaresFrame1 = h.Canvas.LastFrameCheckerSquaresForTest;
        Battery.Assert(scansFrame1 <= convertsFrame1 && convertsFrame1 >= 1,
            $"第一帧扫了 {scansFrame1} 次、冷块 {convertsFrame1} 块：判定次数不许超过真正第一次上屏的块数，" +
            "而冷块必须不为 0——0 冷块时下面那句「全命中的帧一次都不扫」是恒真");

        int warmFrames = 0, warmScans = -1, warmTiles = 0, warmSquares = -1;
        var trace = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            long before = h.Canvas.TileOpacityScans;
            h.Render();
            trace.Add($"第{i + 2}帧 贴{h.Canvas.LastBlitDrawTilesForTest} 冷{h.Canvas.LastBlitConvertTilesForTest}"
                + $" 扫{h.Canvas.TileOpacityScans - before} 缓存{h.Canvas.TileBitmapCountForTest}");
            if (h.Canvas.LastBlitConvertTilesForTest != 0) continue;
            warmFrames = i + 1;
            warmScans = (int)(h.Canvas.TileOpacityScans - before);
            warmTiles = h.Canvas.LastBlitDrawTilesForTest;
            warmSquares = h.Canvas.LastFrameCheckerSquaresForTest;
            break;
        }
        Battery.Assert(warmScans == 0 && warmTiles >= 1,
            $"稳态帧（{string.Join(" ｜ ", trace)}）：贴了 {warmTiles} 块、判定还扫了 {warmScans} 次——" +
            "全命中的帧必须一次都不扫。第一版把判定单独记了一张表，判据当场读到" +
            "「冷块 0 却仍扫 1 次」：修订号一变，位图缓存在帧中途被整块清掉，判定表也被清了，" +
            "而那张位图记录还在 ⇒ 两张表两个寿命就会有其中一张先死的帧。" +
            "现在判定与位图是同一条记录，这条才恒成立");
        Battery.Assert(warmScans >= 0,
            "连画五帧没有出现一帧「零冷块」的稳态帧 ⇒ 上面那句「全命中的帧不重扫」压根没被判过");
        Battery.Assert(warmScans == 0 && warmTiles >= 1,
            $"第 {warmFrames} 帧稳态：贴了 {warmTiles} 块、判定还扫了 {warmScans} 次：" +
            "全命中的帧必须一次都不扫，那张与位图缓存同寿命的判定表才是真的复用");

        IReadOnlyCollection<RgbaImage> cached = h.Canvas.CachedTilesForTest;
        Battery.Assert(tilesFrame1 >= 1 && cached.Count >= 1,
            $"第一帧贴了 {tilesFrame1} 块瓦片、位图缓存里挂着 {cached.Count} 块——一块都没有时，" +
            "下面那句「棋盘格 0 格」是恒真，判据抓不到任何东西");
        bool allOpaque = true;
        foreach (RgbaImage tile in cached)
            if (!tile.IsFullyOpaque()) { allOpaque = false; break; }
        Battery.Assert(allOpaque,
            "夹具自证：缓存里的瓦片有带透明的（Core 独立数出来的）——" +
            "那「棋盘格 0 格」就不是省电而是漏画，这一格的前提不成立");
        Battery.Assert(squaresFrame1 == 0 && warmSquares == 0,
            $"贴的是全不透明的瓦片，第一帧／稳态帧却铺了 {squaresFrame1}／{warmSquares} 格棋盘格：" +
            "这些格子马上被同样不透明的位图整个盖住，屏上看不出、每帧照铺——正是这一改要省的那一笔");
        Battery.Evidence($"第一帧贴 {tilesFrame1} 块（冷 {convertsFrame1}）／扫 {scansFrame1} 次／铺 {squaresFrame1} 格；" +
                         $"第 {warmFrames} 帧稳态贴 {warmTiles} 块（冷 0）／扫 {warmScans} 次／铺 {warmSquares} 格；" +
                         $"进程累计铺过 {h.Canvas.CheckerCalls} 次／{h.Canvas.CheckerSquares} 格（真实截图这一族不该涨）");
    }

    /// <summary>
    /// 反证（精度红线 §3）。正判据抓的假写法是<b>"跳过"写得太宽</b>：把带透明的瓦片也判成不透明，
    /// 屏上就出现"透明处露出画布底色"的缺陷——那一族缺陷在真实截图上看不见（截图全不透明），
    /// 所以这一格自己造两块瓦片喂给产品那一个判定入口，要求<b>一真一假</b>：
    /// 全 255 的报"不必铺"、只改一个像素 alpha 的报"要铺"。
    /// 两头都判防的是同一件事：判定写成恒 <c>true</c>（永远跳过）或恒 <c>false</c>（永远铺）都能让
    /// 正判据那一格看起来正常——恒真时方格数恰好 0、恒假时方格数会涨但没人拿它当失败。
    /// 另外钉一枚寿命：判定结果与位图缓存<b>同进同退</b>，整块作废之后同一块必须重扫一次。
    /// </summary>
    private static void CheckerSkipMustNotSwallowTransparentTiles()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();

        var opaque = new RgbaImage(32, 32);
        for (int i = 3; i < opaque.Pixels.Length; i += 4) opaque.Pixels[i] = 255;
        var holed = new RgbaImage(32, 32);
        for (int i = 3; i < holed.Pixels.Length; i += 4) holed.Pixels[i] = 255;
        holed.Pixels[7] = 0;      // 只有一个像素透明：这正是"补透明"要露出棋盘格的那一类

        Battery.Assert(!h.Canvas.TileNeedsCheckerForTest(opaque),
            "全不透明那块被判成「要铺棋盘格」：判定恒假，这一改等于没做，方格数照旧每帧涨");
        Battery.Assert(h.Canvas.TileNeedsCheckerForTest(holed),
            "带一个透明像素那块被判成「不必铺」：判定恒真 ⇒ 透明处露出的是画布底色，" +
            "而真实截图全不透明、屏上永远看不出来——只有这一格会红");

        long scansBefore = h.Canvas.TileOpacityScans;
        Battery.Assert(!h.Canvas.TileNeedsCheckerForTest(opaque) && h.Canvas.TileOpacityScans == scansBefore,
            $"同一块第二次判定又扫了一遍（{scansBefore} → {h.Canvas.TileOpacityScans}）：" +
            "判定没进那张与位图缓存同寿命的表，省下的格子会换成每帧一次全块扫描");

        // 整块作废之后必须重扫：推进修订号让产品在下一帧清掉位图缓存（判定表与它同寿命）
        h.Render();
        h.P.Mutate("改一下重叠让修订号前进", _ =>
        {
            Seam seam = h.P.Doc.Seams[0];
            seam.SetOverlap(seam.OverlapLength + 1);
        });
        h.Render();
        Battery.Assert(h.Canvas.TileOpacityScans > scansBefore,
            $"整块作废之后重画那两帧，判定一次都没重扫（仍是 {h.Canvas.TileOpacityScans} 次）：" +
            "判定表活过了它描述的那批像素 ⇒ 下一批瓦片拿到的是上一批的「不必铺」，" +
            "那是把该露出棋盘格的透明处永久画成不透明");
        Battery.Evidence($"两块新瓦片一真一假（判定入口各自答对）；同一块第二次判定不重扫；" +
                         $"推进修订号后判定重扫到 {h.Canvas.TileOpacityScans} 次");
    }

    /// <summary>读面板文本里以 prefix 开头的那一行（找不到就把"没有这一行"原样交回去，让断言报得出来）。</summary>
    private static string HudLine(string text, string prefix)
    {
        foreach (string line in text.Split('\n'))
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        return $"（面板里没有以 {prefix} 开头的行）";
    }

    /// <summary>第二十九改的核对单位：<b>一行里的一个值</b>，不是整段文本。反证那一格钉的就是这个区别。</summary>
    private static bool HudLineSays(string text, string prefix, string expected) =>
        HudLine(text, prefix).Contains(expected, StringComparison.Ordinal);

    /// <summary>
    /// 第二十九改（正判据）。需求三第 4 条点名六个量"只有它们变化时才重绘 HUD"。
    /// 闸门那一侧（静止二十帧只重建一次）早就有格钉着，缺的是另一半：<b>这六个量各自变了，
    /// 屏上对应那一行必须跟着换</b>——否则"只在它们变化时重绘"会退化成"每 100 毫秒换一张旧图"，
    /// 用户看到的就是停在旧值上的读数（同一族里 <c>guard/面板读数不跟修订号刷新则停在旧值</c> 钉的是修订号那条通路）。
    /// 六个量并不互相独立（改对齐会连带改重叠、带矩形、画布高），所以本格<b>一次只动一个量、
    /// 只核它自己那一行读出的具体值</b>（期望值从模型现取，不抄第二份算术）。
    /// 第六个量（当前帧耗时）没有参数可动，形状是"什么都不动、越过闸门再画，那行必须自己刷新"；
    /// 一次撞值不算红（毫秒按 F1 打印），连试四帧都不变才算。
    /// </summary>
    private static void HudSixTriggersEachMoveTheirOwnLine()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.SelectSeam(h.Current.Id);
        h.Canvas.ShowDebugHud = true;
        h.Render();

        var moved = new List<string>();
        var stale = new List<string>();
        long rebuildsBefore = h.Canvas.HudPanelRebuilds;

        void Trigger(string what, string prefix, Action apply, Func<string> expected)
        {
            apply();
            System.Threading.Thread.Sleep(150);      // 越过 10Hz 闸门：这一格测「值换没换」，不测闸门放不放行
            h.Render();
            string want = expected();
            string line = HudLine(h.Canvas.HudTextForTest, prefix);
            if (line.Contains(want, StringComparison.Ordinal)) moved.Add($"{what} 读出 {want}");
            else stale.Add($"{what}：期望「{want}」，屏上那行是「{line}」");
        }

        Trigger("视口原点", "Viewport.TopLeft=",
            () => h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(0, h.Canvas.Viewport.TopLeft.Y + 11)),
            () => $"=({h.Canvas.Viewport.TopLeft.X},{h.Canvas.Viewport.TopLeft.Y})");
        Trigger("缩放", "Viewport.TopLeft=",
            () => h.Canvas.Viewport.SetZoom(h.Canvas.Viewport.Zoom * 1.25),
            () => $"zoom={h.Canvas.Viewport.Zoom:F3}");
        Trigger("对齐", "AlignmentOffset=",
            () => h.P.Mutate("改对齐", _ => h.Current.ApplyAlignment(-30)),
            () => $"=({h.Current.AlignmentOffset.X},{h.Current.AlignmentOffset.Y})");
        Trigger("蓝线", "CutY=",
            () => h.P.Mutate("改蓝线", _ => h.Current.ApplyCut(35)),
            () => $"={h.P.Layout.DescribeSeam(h.Current)!.CutPosition}/");
        Trigger("重叠带", "OverlapRect=",
            () => h.P.Mutate("改重叠", _ => h.Current.SetOverlap(h.Current.OverlapLength + 25)),
            () =>
            {
                IntRect r = h.P.Layout.DescribeSeam(h.Current)!.OverlapRect;
                return $"=({r.X},{r.Y},{r.Width},{r.Height})";
            });

        string frameBefore = HudLine(h.Canvas.HudTextForTest, "帧 最近");
        string frameAfter = frameBefore;
        for (int i = 0; i < 4 && frameAfter == frameBefore; i++)
        {
            System.Threading.Thread.Sleep(150);
            h.Render();
            frameAfter = HudLine(h.Canvas.HudTextForTest, "帧 最近");
        }
        if (frameAfter != frameBefore) moved.Add($"当前帧耗时 {frameBefore} → {frameAfter}");
        else stale.Add($"连画四帧（每帧之间越过闸门）整行仍是「{frameAfter}」：只随时间变的那一行没刷新");

        Battery.Assert(moved.Count == 6,
            $"本格只核到 {moved.Count} 个量（应当恰好 6 个：视口原点、缩放、对齐、蓝线、重叠带、当前帧耗时）：" +
            "少核一个，这一格就只是过了，不是需求三点名那六项都核过");
        Battery.Assert(stale.Count == 0, "这些行停在旧值上：\n  " + string.Join("\n  ", stale));
        Battery.Assert(h.Canvas.HudPanelRebuilds > rebuildsBefore,
            "六次触发之后面板一次都没重建（重建计数没涨）：那上面六行新值是从哪来的？" +
            "要么这个计数不记这条通路，要么本格读的是同一帧的旧文本");
        Battery.Evidence($"六个触发量各自换掉自己那一行：{string.Join("；", moved)}");
    }

    /// <summary>
    /// 反证（精度红线 §3）。上一格一格核六行，最容易的假绿是<b>拿"整段文本变了"顶替"那一行变了"</b>：
    /// 改蓝线会连带把带矩形、屏上矩形那些行一起改掉（带宽随切点走），所以"CutY 那一行停在旧值"这种缺陷
    /// 在整段比较下是过的。这一格从<b>真面板</b>取改前／改后两份文本，把改后那份里的 CutY 行换回改前那一行，
    /// 然后要求：逐行核对点出它（false），而整段比较说"变了"（true，这就是要写下来的漏洞）。
    /// 两头都从现量文本构造，不写死任何字符串。
    /// </summary>
    private static void PerLineCheckCatchesOneStaleValue()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.SelectSeam(h.Current.Id);
        h.Canvas.ShowDebugHud = true;
        h.Render();
        string before = h.Canvas.HudTextForTest;

        h.P.Mutate("改蓝线", _ => h.Current.ApplyCut(35));
        System.Threading.Thread.Sleep(150);
        h.Render();
        string after = h.Canvas.HudTextForTest;
        string want = $"={h.P.Layout.DescribeSeam(h.Current)!.CutPosition}/";
        Battery.Assert(HudLineSays(after, "CutY=", want),
            $"夹具自证：真面板里 CutY 那行本该读出 {want}，实际「{HudLine(after, "CutY=")}」——" +
            "这里就不对的话，下面那句点得出旧值就是恒真");

        string staleLine = HudLine(before, "CutY=");
        var patched = new List<string>();
        foreach (string line in after.Split('\n'))
            patched.Add(line.StartsWith("CutY=", StringComparison.Ordinal) ? staleLine : line);
        string doctored = string.Join("\n", patched);

        Battery.Assert(!HudLineSays(doctored, "CutY=", want),
            "把 CutY 那一行换回旧值之后，逐行核对仍然说这一行是新值 ⇒ 核对根本没在看那一行");
        Battery.Assert(doctored != after && doctored != before,
            "夹具自证：伪造版本必须与改前、改后两份都不同——" +
            "只有与改前也不同，「整段比较说变了」才是这格要点出来的漏洞");
        Battery.Evidence($"整段比较：doctored 与改前／改后都不同（它会说刷新过了）；" +
                         $"逐行核对：CutY 那行读「{HudLine(doctored, "CutY=")}」，不含 {want} ⇒ 点出来了");
    }

    // ─────────────── ⓪e 叠层那一笔要能再拆到底（第五改：拆账，不是换个名目再报一遍） ───────────────

    /// <summary>
    /// 第四改把“其余”切成叠层与帧前之后，叠层仍然是一整笔：真窗口里它读过 0.4、也读过 56.7，
    /// 而那两次的病因完全不同（56.7 那一枚带着回收标记——回收的钱落在它发生时正在被计时的那一段）。
    /// 这一格给 PaintCore 里那六个从没单独计时的调用各开一列，判三件事：
    /// ① 六段＋残差必须等于叠层那一笔（恒等式由产品自己减出来，判据只核对）；
    /// ② 残差必须小——残差大就是“开了几个计时器但没真盖住那些调用”，拆账等于没拆；
    /// ③ 背景／预览／拼缝这三段必须真读到东西（全 0 的拆账是假拆）。
    /// 反证在 <c>guard/拆账必须跟着开关动</c>：那一格把开关关掉，量“哪一段跟着变 0、哪一段不许变”。
    /// </summary>
    private static void OverlaySplitAccountsWithTheOverlayBucket()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowPatchOverlay = true;
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.ShowDebugHud = true;
        h.Render();
        h.Render();
        StitchCanvasControl.OverlayNumbers o = h.Canvas.LastOverlayNumbersForTest
            ?? throw new InvalidOperationException("画完一帧没留下叠层拆账（第五改的结算没接上）");
        StitchCanvasControl.StageNumbers n = h.Canvas.LastStageNumbersForTest
            ?? throw new InvalidOperationException("画完一帧没留下分阶段数值（结算没接上）");

        double subs = o.Background + o.Preview + o.Seams + o.Patch + o.Badge + o.Pick;
        Battery.Assert(Math.Abs(subs + o.Residual - o.Overlay) < 0.05,
            $"六段 {subs:F3} ＋ 残差 {o.Residual:F3} 必须等于叠层那一笔 {o.Overlay:F3}ms" +
            "——对不上就是有一笔既进了拆账又进了叠层（同一笔钱报两遍）");
        Battery.Assert(o.Residual >= -0.005,
            $"残差读到 {o.Residual:F3}ms：负数＝某段的计时把叠层之外的钱也圈进来了（比如把瓦片那三段算重）");
        // ②残差要小：拆账必须盖住叠层的绝大部分，否则“拆”只是开了个名字。
        double cover = subs / o.Overlay;
        Battery.Assert(o.Residual <= Math.Max(0.30, o.Overlay * 0.35),
            $"残差 {o.Residual:F2}ms 占叠层 {o.Overlay:F2}ms 的 {(1 - cover) * 100:F0}%——" +
            "残差这么大说明有些没计时的调用没被这六列盖住（第五改就白做了）");
        foreach ((string name, double v) in new (string, double)[]
                 { ("背景", o.Background), ("预览", o.Preview), ("拼缝", o.Seams) })
            Battery.Assert(v > 0,
                $"{name} 这一列读到 {v:F4}ms：背景填充、重叠预览、拼缝线这三段每帧都真在画，" +
                "读到 0 就是计时点没夹住那次调用");
        Battery.Evidence($"这一帧叠层 {o.Overlay:F2}ms ＝ 背景 {o.Background:F2} ＋ 预览 {o.Preview:F2} ＋ 拼缝 {o.Seams:F2} " +
                         $"＋ 补丁 {o.Patch:F2} ＋ 角标 {o.Badge:F2} ＋ 点选 {o.Pick:F2} ＋ 残差 {o.Residual:F2}ms" +
                         $"（覆盖率 {cover * 100:F0}%，整帧 {n.Frame:F2}ms）");
    }

    /// <summary>
    /// 反证（精度红线 §3）：拆账那六列必须<b>各自跟着自己那次调用</b>动——把重影对比层关掉，
    /// “预览”那一列就要真的掉到 0，而“拼缝”照旧有东西画（拼缝线不属于那几个开关）。
    /// 这一条防两种假拆：① 六列其实是同一笔钱复制出来的（关掉开关它们不会跟着变）；
    /// ② 计时点夹错了地方（关掉开关后那一列仍然有数，或本来该有数的拼缝反而空了）。
    /// </summary>
    private static void OverlaySplitTracksWhichLayersWereDrawn()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowPatchOverlay = true;
        h.Canvas.ShowCompareOverlay = true;
        h.Canvas.ShowDebugHud = true;
        h.Render();
        h.Render();
        StitchCanvasControl.OverlayNumbers on = h.Canvas.LastOverlayNumbersForTest
            ?? throw new InvalidOperationException("开着叠层画完一帧没留下拆账");
        Battery.Assert(on.Preview > 0,
            $"夹具自证：开着对比层时“预览”那一列得有数（读到 {on.Preview:F4}ms）");

        h.Canvas.ShowPatchOverlay = false;
        h.Canvas.ShowCompareOverlay = false;
        h.Canvas.ShowDebugHud = false;
        h.Render();
        StitchCanvasControl.OverlayNumbers off = h.Canvas.LastOverlayNumbersForTest
            ?? throw new InvalidOperationException("关掉叠层画完一帧没留下拆账");
        Battery.Assert(off.Preview < 0.05,
            $"关掉重影对比层之后“预览”这一列还读到 {off.Preview:F3}ms——那列是假拆" +
            "（计时点夹在别处，或者这一列跟那层根本不同源）");
        Battery.Assert(off.Seams > 0,
            $"关掉叠层后“拼缝”这一列读到 {off.Seams:F4}ms：拼缝线不属于那几个开关，它必须照旧有数" +
            "（掉到 0 说明这一列跟着错的东西动了）");
        double subs = off.Background + off.Preview + off.Seams + off.Patch + off.Badge + off.Pick;
        Battery.Assert(Math.Abs(subs + off.Residual - off.Overlay) < 0.05,
            $"关掉叠层那一帧的恒等式也得成立（六段 {subs:F3} ＋ 残差 {off.Residual:F3} vs 叠层 {off.Overlay:F3}）");
        Battery.Evidence($"开着：叠层 {on.Overlay:F2}ms（预览 {on.Preview:F2}、拼缝 {on.Seams:F2}）；" +
                         $"关掉：叠层 {off.Overlay:F2}ms（预览 {off.Preview:F2}、拼缝 {off.Seams:F2}）" +
                         "——预览那一列跟着开关走、拼缝那一列不跟");
    }

    /// <summary>
    /// 拆账量到"拼缝是叠层里最大的一段"之后，本条钉住那次优化的兑现：状态标签的宽度只量一次。
    /// 判据不看耗时（耗时是机器的），只看<b>真的去量的次数</b>与<b>命中次数</b>这两个计数：
    /// 第一帧必须真量过（≥1 次），第二帧新增必须为 0 而命中必须涨（否则要么没画标签、要么缓存没接上）。
    /// 键的组成由反证 <c>guard/标签宽度缓存的键必须含字体与 DPI</c> 那一格管。
    /// </summary>
    private static void SeamLabelWidthIsMeasuredOnce()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        // 夹具：新建的缝默认是 Pending——屏上不画状态标签，那一格就成了空判。
        // 本格第一次跑就是这么红的（命中 11 → 11，一次都没涨），所以先把状态落到会画标签的那几档里。
        h.Current.Status = SeamStatus.NeedsReview;
        h.Render();                    // 热身：静态缓存可能早被别的格子建好了，判据不许依赖跑次顺序
        int m0 = StitchCanvasControl.SeamLabelMeasureCountForTest;
        int hit0 = StitchCanvasControl.SeamLabelCacheHitsForTest;
        h.Render();
        int m1 = StitchCanvasControl.SeamLabelMeasureCountForTest;
        int hit1 = StitchCanvasControl.SeamLabelCacheHitsForTest;
        Battery.Assert(hit1 > hit0,
            $"这一帧的命中次数没涨（{hit0} → {hit1}）：说明标签压根没画，那一格就成了空判" +
            "（夹具自证：这条缝的状态得落在会画标签的那几档里）");
        Battery.Assert(m1 == m0,
            $"缓存建好之后这一帧又新量了 {m1 - m0} 次宽度（累计 {m0} → {m1}）：" +
            "同一个标签、同一支字体、同一个 DPI 本该走缓存——16 条缝每帧各量一次的钱就是这么来的");
        Battery.Evidence($"热身后一帧：真测量新增 {m1 - m0} 次、命中新增 {hit1 - hit0} 次、" +
                         $"表里 {StitchCanvasControl.SeamLabelCacheEntriesForTest} 条（键＝文字×字体×DPI）");
    }

    /// <summary>
    /// 反证（精度红线 §3）：缓存的键必须把<b>字体与 DPI</b>算进去。
    /// 只按文字建键的实现会命中同一句在另一种字体下的旧值——盒子宽度于是跟着错的字体走，
    /// 那正是需求四复看时量到的"标签被截掉"那一族。字体那一维直接比宽度；
    /// DPI 那一维比"有没有另开一格"（同一句标签在两个 DPI 下宽度可能恰好一样，
    /// 判宽度会把这一半判成空绿）。
    /// </summary>
    private static void SeamLabelCacheKeyIncludesFontAndDpi()
    {
        using var bmp = new System.Drawing.Bitmap(64, 64);
        using System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp);
        const string label = "已通过";
        using var fontSmall = new System.Drawing.Font("Microsoft YaHei UI", 8.5f);
        using var fontBig = new System.Drawing.Font("Microsoft YaHei UI", 20f);
        var freshSmall = System.Windows.Forms.TextRenderer.MeasureText(g, label, fontSmall);
        var freshBig = System.Windows.Forms.TextRenderer.MeasureText(g, label, fontBig);
        Battery.Assert(freshSmall.Width < freshBig.Width,
            $"夹具自证：同一句标签在 8.5 与 20 字号下得量出不同宽度（{freshSmall.Width} vs {freshBig.Width}），" +
            "否则这一格判不出键里有没有字体");

        int before = StitchCanvasControl.SeamLabelCacheEntriesForTest;
        var cachedSmall = StitchCanvasControl.MeasureSeamLabel(g, label, fontSmall);
        var cachedBig = StitchCanvasControl.MeasureSeamLabel(g, label, fontBig);
        Battery.Assert(cachedSmall == freshSmall && cachedBig == freshBig,
            $"缓存返回 {cachedSmall.Width}／{cachedBig.Width}，现场直接量的是 {freshSmall.Width}／{freshBig.Width}" +
            "——后一个要是等于前一个，就是键里漏了字体");
        Battery.Assert(StitchCanvasControl.SeamLabelCacheEntriesForTest >= before + 2,
            $"同一句标签两种字体必须在表里占两格（起 {before} → 现 " +
            $"{StitchCanvasControl.SeamLabelCacheEntriesForTest}）：只占一格同样是键里漏了字体");

        using var bmp2 = new System.Drawing.Bitmap(64, 64);
        bmp2.SetResolution(192f, 192f);
        using System.Drawing.Graphics g2 = System.Drawing.Graphics.FromImage(bmp2);
        Battery.Assert(Math.Abs(g2.DpiX - 192f) < 1f,
            $"夹具自证：这块 Graphics 得真带上 192 DPI（读到 {g2.DpiX:F0}），否则 DPI 那一半没测到东西");
        int beforeDpi = StitchCanvasControl.SeamLabelCacheEntriesForTest;
        StitchCanvasControl.MeasureSeamLabel(g2, label, fontSmall);
        Battery.Assert(StitchCanvasControl.SeamLabelCacheEntriesForTest >= beforeDpi + 1,
            $"换到 192 DPI 再量同一句标签，表里没多出一格（{beforeDpi} → " +
            $"{StitchCanvasControl.SeamLabelCacheEntriesForTest}）：键里漏了 DPI，" +
            "高分屏上就会拿回 96 DPI 的旧宽度——那正是标签盒子比字窄、整行被裁那一族");
        Battery.Evidence($"同一句标签：8.5 字号 {cachedSmall.Width}×{cachedSmall.Height}、" +
                         $"20 字号 {cachedBig.Width}×{cachedBig.Height}，换 DPI 另开一格" +
                         "（键＝文字×字体×DPI）");
    }

    /// <summary>
    /// 第七改：一条远在屏外的缝，它的把手与状态标签<b>一个像素都不会落在屏上</b>，
    /// GDI 把它们裁掉之前，测量与调用已经白烧了一遍（25% 档现量到 拼缝段 6.7ms／整帧约 27ms）。
    /// 本格两头各判一次：缝看得见时必须真画（画次数涨），滚到屏外时必须早退（跳次数涨、画次数不涨）。
    /// 反证 <c>guard/屏外早退不许粘住可见的那一条</c> 判"滚回来还得照画"——把跳过做成一次性旗标的实现会被它抓到。
    /// </summary>
    private static void OffScreenSeamsSkipTheirChrome()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Current.Status = SeamStatus.NeedsReview;      // Pending 不画标签（夹具自证，见上一格）
        int d0 = StitchCanvasControl.SeamChromeDrawnForTest;
        int s0 = StitchCanvasControl.SeamChromeSkippedForTest;
        h.Render();
        int d1 = StitchCanvasControl.SeamChromeDrawnForTest;
        Battery.Assert(d1 > d0,
            $"缝就在眼前时画次数没涨（{d0} → {d1}）：早退把看得见的那条也跳了，屏上会少了把手与状态标签");
        // 滚到很远（这条缝的线在画布上离起点不到几百像素，滚 6000 一定出屏）
        h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft + new IntPoint(0, 6000));
        h.Render();
        int d2 = StitchCanvasControl.SeamChromeDrawnForTest;
        int s2 = StitchCanvasControl.SeamChromeSkippedForTest;
        Battery.Assert(s2 > s0,
            $"缝滚到屏外之后一次都没早退（跳次数 {s0} → {s2}）：25% 档那种一屏十几块瓦片的形状下，" +
            "每条屏外的缝还在各烧一次文字渲染与两次填充");
        Battery.Assert(d2 == d1,
            $"缝已经屏外，画次数还在涨（{d1} → {d2}）：早退没生效，或者判据用的是别的矩形");
        Battery.Evidence($"看得见：画 +{d1 - d0}；滚到屏外：画 +{d2 - d1}、跳过 +{s2 - s0}" +
                         "（跳的是整块不落在可见区里的那些，屏上内容一字不变）");
    }

    /// <summary>
    /// 反证（精度红线 §3）：早退必须是<b>每帧重判</b>，不是一次性旗标。
    /// 做法是"滚出去 → 滚回来 → 再画一次"，要求滚回来那一帧画次数照涨；
    /// 又把视口停在屏外连画两帧，要求这两帧的画次数一字不变（不涨）而跳次数继续涨——
    /// 任何"第一次跳之后就记住跳"的实现都会在这里红。
    /// </summary>
    private static void OffScreenSkipIsRecomputedEveryFrame()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Current.Status = SeamStatus.NeedsReview;
        h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft + new IntPoint(0, 6000));
        h.Render();
        int d0 = StitchCanvasControl.SeamChromeDrawnForTest;
        int s0 = StitchCanvasControl.SeamChromeSkippedForTest;
        h.Render();                                       // 还在屏外：不该有任何东西被画
        int d1 = StitchCanvasControl.SeamChromeDrawnForTest;
        int s1 = StitchCanvasControl.SeamChromeSkippedForTest;
        Battery.Assert(d1 == d0, $"缝在屏外却画了 {d1 - d0} 次（{d0} → {d1}）");
        Battery.Assert(s1 > s0, $"屏外第二帧的跳次数没涨（{s0} → {s1}）：早退被记成一次性旗标了？");
        h.Canvas.Viewport.MoveTopLeftTo(h.Canvas.Viewport.TopLeft - new IntPoint(0, 6000));
        h.Render();                                       // 滚回来：必须照画
        int d2 = StitchCanvasControl.SeamChromeDrawnForTest;
        Battery.Assert(d2 > d1,
            $"缝滚回屏内之后画次数仍不涨（{d1} → {d2}）：早退粘住了，屏上会一直没有把手与状态标签");
        Battery.Evidence($"屏外两帧：画 {d0}→{d1}（不涨）、跳过 {s0}→{s1}（每帧都判）；滚回屏内：画 +{d2 - d1}");
    }

    // ─────────────── ⓪g 对照层只取屏上看得见的那几行（第八改） ───────────────

    /// <summary>
    /// 第八改动的是"画出去多少像素"，所以这一格判的是<b>像素</b>，不是耗时：
    /// 一个错行的对照层照样很快，甚至更快——只看耗时的话这一改根本没法验收。
    ///
    /// 三件事一头不少：
    /// ① <b>真的裁了</b>：把可见区压到带的一半以下（放大 8 倍 ⇒ 一屏只有几十行画布），
    ///    这一帧的 shown 必须严格短于 band。不裁的话下面那两个恒等式就退化成自己跟自己比（空判），
    ///    所以这条放在最前面当夹具自证；
    /// ② <b>裁了不改像素</b>：以"不裁的整带"为参照（那是改前的行为），
    ///    被裁出来的那份必须与参照里对应的那几行<b>逐字节相同</b>。
    ///    取数走产品自己的 <see cref="StitchCanvasControl.DiscardedCopyForTest"/>——
    ///    判据不许再抄一遍换算逻辑，否则它只是在证明自己写的第二份代码；
    /// ③ <b>带整条在屏外就一笔不取</b>：shown 必须是空、band 仍要报得出（说明是"裁没了"不是"没算"）。
    ///
    /// 顺带把 survivor 那一头也比上（同一个函数里的另一次分配）：显示侧裁矩形同样不许让它错行。
    /// </summary>
    private static void CompareLayerClipKeepsEveryRowIdentical()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();                       // DeviceSize 要有真数，否则可见区量不出来
        h.Canvas.ShowCompareOverlay = true;
        CanvasLayout layout = h.P.Layout;
        Seam seam = h.Current;
        SeamGeometry geo = layout.DescribeSeam(seam)
            ?? throw new InvalidOperationException("夹具自证：这条缝算不出几何");
        PlacedSegment before = layout.Find(seam.BeforeId)!;
        PlacedSegment after = layout.Find(seam.AfterId)!;
        IntRect band = geo.BandRect;
        Battery.Assert(!band.IsEmpty && band.Height > 8,
            $"夹具自证：重叠带得有行数（读到 {band.Height} 行），否则后面全是空判");

        // ① 把可见区压到带以下：放大 8 倍 ⇒ 500 设备像素只覆盖 62 行画布
        h.Canvas.Viewport.SetZoom(8.0);
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X, band.Y + 10));
        h.Render();
        IntRect shown = h.Canvas.LastCompareShownForTest;
        IntRect reported = h.Canvas.LastCompareBandForTest;
        Battery.Assert(reported.X == band.X && reported.Y == band.Y
                       && reported.Width == band.Width && reported.Height == band.Height,
            $"这一帧报回的整带是 {reported}，布局里的带是 {band}——两者必须同一个出处，" +
            "否则「裁的是哪条带」就没有唯一答案");
        Battery.Assert(shown.Height > 0 && shown.Height < band.Height,
            $"这一帧没裁（shown {shown.Height} 行 vs 带 {band.Height} 行）⇒ 第八改没接上，" +
            "后面的逐字节恒等式就只是自己跟自己比");
        Battery.Assert(shown.Y >= band.Y && shown.Bottom <= band.Bottom,
            $"shown {shown} 必须落在带 {band} 之内——超出就说明裁出来的矩形不是带的一段");

        // ② 逐字节恒等：参照 = 不裁的整带（改前的行为），被测 = 产品按可见区取的那一段。
        // 两个窗口各查一遍。放大到 8 倍时一屏只有 50 列画布、而带宽 60 列 ⇒ 横向也会被切，
        // 所以第二个窗口把视口横着再挪 7 列，让 x 偏移那一条换算（left）也被真量到。
        // ═══ 本格第一版就是在这里红的 ═══
        // 我原本假设"裁的只是行、宽度不许变"，被自己的判据打红（期望 60、实际 50）：
        // 产品裁的是矩形，不是横带。修的是判据的假设（把 x 一起算进换算），
        // 不是把产品改成只裁行——只裁行的话每帧仍然要为屏外的列分配与遍历。
        int top = shown.Y - band.Y;
        RgbaImage full = h.Canvas.DiscardedCopyForTest(before, after, geo, band, band)
            ?? throw new InvalidOperationException("夹具自证：整带拼不出对照图");
        var probe = new CanvasComposer(h.R);
        RgbaImage survivorFull = probe.RenderBand(layout, band);

        // 按窗口比一遍，两张对照图各返回第一个对不上的行号；-1 = 全对，-2 = 连矩形尺寸都对不上。
        // 为什么分开报：alt（被裁掉那两份拼的）与 survivor（屏上正常合成那半）走两条不同的取数通路
        // （RenderDebugSource 对比 RenderBand），混在一个读数里就指认不了是哪一条错行——
        // 本格第一次红就是这么红的：只报"第 0 行不同"，看不出错在 survivor 还是 alt。
        //
        // ═══ 这里的 ×4 不是笔误，是第二次踩坑留下的 ═══
        // Row(y) 交回来的是<b>字节</b>跨度，而 left / width 是<b>列</b>：
        // 第一次写成 Slice(left, cut.Width) 时两根 span 一边 50 字节、一边 200 字节，
        // SequenceEqual 恒 false，于是这一格"每行都错"——报的是判据自己的算术错，
        // 不是产品错行。列→字节只能在这一处乘一次（写进注释，别让它下次又被"简化"掉）。
        int BadAlt(IntRect win, RgbaImage cut)
        {
            int t = win.Y - band.Y, leftBytes = (win.X - band.X) * 4, wide = cut.Width * 4;
            if (cut.Width != win.Width || cut.Height != win.Height) return -2;
            if (leftBytes + wide > full.Width * 4 || t + cut.Height > full.Height) return -2;
            for (int i = 0; i < cut.Height; i++)
                if (!full.Row(t + i).Slice(leftBytes, wide).SequenceEqual(cut.Row(i))) return i;
            return -1;
        }
        int BadSurvivor(IntRect win, RgbaImage survivorCut)
        {
            int t = win.Y - band.Y, leftBytes = (win.X - band.X) * 4, wide = survivorCut.Width * 4;
            if (survivorCut.Width != win.Width || survivorCut.Height != win.Height) return -2;
            if (leftBytes + wide > survivorFull.Width * 4 ||
                t + survivorCut.Height > survivorFull.Height) return -2;
            for (int i = 0; i < survivorCut.Height; i++)
                if (!survivorFull.Row(t + i).Slice(leftBytes, wide)
                        .SequenceEqual(survivorCut.Row(i))) return i;
            return -1;
        }

        RgbaImage cut1 = h.Canvas.DiscardedCopyForTest(before, after, geo, band, shown)
            ?? throw new InvalidOperationException("裁到可见区之后对照图拼不出来了（少画＝功能没了）");
        RgbaImage survivor1 = probe.RenderBand(layout, shown);
        int bad1 = BadAlt(shown, cut1), bad1s = BadSurvivor(shown, survivor1);
        Battery.Assert(bad1 == -1,
            bad1 == -2
                ? $"第一个窗口里 alt 的尺寸（{cut1.Width}×{cut1.Height}）与窗口（{shown.Width}×{shown.Height}）不一致"
                : $"alt 在第 {bad1} 行（带的第 {top + bad1} 行、列 {shown.X - band.X} 起）与不裁的那一份逐字节不同 " +
                  "⇒ 被裁掉那两份的行号换算错了，屏上会显示错行的对照" +
                  "（比少画几行更糟：它看起来在工作，给的是错的结论）");
        Battery.Assert(bad1s == -1,
            bad1s == -2
                ? $"第一个窗口里 survivor 的尺寸（{survivor1.Width}×{survivor1.Height}）与窗口不一致"
                : $"正常合成那一份（屏上正显示着的半张）在第 {bad1s} 行对不上 ⇒ 显示侧裁矩形改动了像素，" +
                  "与红线二「分块与整块逐字节一致」冲突，得先查 RenderBand 的换算");

        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X + 7, band.Y + 10));
        h.Render();
        IntRect win2 = h.Canvas.LastCompareShownForTest;
        Battery.Assert(win2.X == band.X + 7 && win2.Width > 0 && win2.Width < band.Width,
            $"第二个窗口本该横向也切一段（带 {band.X}..{band.Right}，读到 {win2}）——" +
            "x 没切的话 left 那一条换算就没被测到");
        RgbaImage cut2 = h.Canvas.DiscardedCopyForTest(before, after, geo, band, win2)
            ?? throw new InvalidOperationException("第二个窗口拼不出对照图");
        int bad2 = BadAlt(win2, cut2), bad2s = BadSurvivor(win2, probe.RenderBand(layout, win2));
        Battery.Assert(bad2 == -1,
            bad2 == -2
                ? $"第二个窗口里 alt 的尺寸与窗口（{win2}）不一致"
                : $"横向挪 {win2.X - band.X} 列之后 alt 第 {bad2} 行对不上 ⇒ 列偏移算了但没落到取数上");
        Battery.Assert(bad2s == -1,
            bad2s == -2 ? "第二个窗口里 survivor 的尺寸与窗口不一致"
                        : $"横向挪 {win2.X - band.X} 列之后 survivor 第 {bad2s} 行对不上");

        // ③ 带整条滚到屏外：这一帧一笔都不该取，但带仍要报得出
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X, band.Bottom + 5));
        h.Render();
        Battery.Assert(h.Canvas.LastCompareShownForTest.IsEmpty,
            $"带在屏外那一帧仍然取了 {h.Canvas.LastCompareShownForTest.Height} 行——这一改的钱就白省了");
        Battery.Assert(!h.Canvas.LastCompareBandForTest.IsEmpty,
            "带在屏外时几何照旧算得出：只该停掉取数，不该把带本身弄没" +
            "（否则下一轮没法区分「裁没了」和「这条缝没了」）");

        // 正向对照：回到能装下整带的缩放，shown 必须又等于整带（证明裁是跟着可见区动的，不是写死的）
        h.Canvas.Viewport.SetZoom(1.0);
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X, band.Y));
        h.Render();
        IntRect wide = h.Canvas.LastCompareShownForTest;
        Battery.Assert(wide.Height == band.Height,
            $"缩放到一屏装得下整带时 shown 该等于带（{band.Height} 行），读到 {wide.Height} 行——" +
            "若恒小于带，说明这一改不是「按可见区裁」而是「固定裁一半」");
        Battery.Evidence($"带 {band.Height} 行；放大 8 倍那帧只取 {shown.Height} 行（从带的第 {top} 行起）逐字节相同；" +
                         $"整条屏外那帧取 0 行；缩回 100% 又取满 {wide.Height} 行");
    }

    /// <summary>
    /// 反证（精度红线 §3）：上面那格的判据是"逐行比字节"，它必须<b>真能红</b>。
    /// 这里不改源文件，而是把同一份对照图按两种"错行的读法"去跟参照比，要求两种都被拒：
    /// · <b>错一行</b>：把 shown 的第 i 行去对参照的第 top+i+1 行（换算里丢了一个 +1／−1 就是这个形状）；
    /// · <b>整张放在 0 行</b>：忽略 top，直接从参照第 0 行起对
    ///   （这是最省事的那种写错：把被裁掉那两份整张取回来盖在左上角）。
    /// 同时必须量到"对的那一种读法一行都不差"——没有这个正向对照，上面两个"抓到"
    /// 可能只是两份东西本来就不一样大（本格第一版就是这么假绿的，见下面那段）。
    /// </summary>
    private static void CompareLayerRowShiftIsCaughtByBytes()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Canvas.ShowCompareOverlay = true;
        CanvasLayout layout = h.P.Layout;
        Seam seam = h.Current;
        SeamGeometry geo = layout.DescribeSeam(seam)!;
        PlacedSegment before = layout.Find(seam.BeforeId)!;
        PlacedSegment after = layout.Find(seam.AfterId)!;
        IntRect band = geo.BandRect;

        h.Canvas.Viewport.SetZoom(8.0);
        // 横向也挪 3 列：left≠0 才会把"列→字节"那次乘法暴露出来
        // （left=0 时 Slice 的起点恰好不用换算，这一格第一版就是这么漏过去的）。
        h.Canvas.Viewport.MoveTopLeftTo(new IntPoint(band.X + 3, band.Y + 10));
        h.Render();
        IntRect shown = h.Canvas.LastCompareShownForTest;
        int top = shown.Y - band.Y, left = shown.X - band.X;
        RgbaImage full = h.Canvas.DiscardedCopyForTest(before, after, geo, band, band)!;
        RgbaImage cut = h.Canvas.DiscardedCopyForTest(before, after, geo, band, shown)!;

        // 三种读法都只比"同宽同高"那一段（列从 left 起、宽 cut.Width），
        // 否则比的就不是同一块矩形，"抓到不同"会沦为"两块大小不一样"。
        // 三个数数的都是<b>对不上的行数</b>（第一版把方向写反：数着相等的行、却断言"有 N 行不同"，
        // 那样这一格只会红在错的地方——本项目踩过的"判据自身缺陷"又一次）。
        int same = 0, diffAtShift = 0, diffFromZero = 0;
        for (int i = 0; i < cut.Height; i++)
        {
            ReadOnlySpan<byte> c = cut.Row(i);
            if (top + i < full.Height && !t1(full, top + i, left, c)) same++;
            if (top + i + 1 < full.Height && !t1(full, top + i + 1, left, c)) diffAtShift++;
            if (i < full.Height && !t1(full, i, left, c)) diffFromZero++;
        }
        Battery.AssertEqual(0, same,
            "正向对照：同一块矩形该逐行都对得上（这个数就是对不上的行数）——" +
            "少了这一句，下面两个「抓到」就可能是「两份本来就不是同一块」");
        Battery.Assert(diffAtShift > 0,
            "错一行的读法居然逐行都对得上 ⇒ 这份素材每一行长得一样，" +
            "上面那格的字节比较是空判（要换 StripedPage 那种逐行不同的素材，不是放宽判据）");
        Battery.Assert(top > 0 && diffFromZero > 0,
            $"「整张放在 0 行」这种错法没被抓到（top={top}、对不上的行数 {diffFromZero}）——" +
            "那说明裁与不裁其实没差别，第八改的恒等式守着个空东西");

        // ═══ 这一格第一版是"假绿"的，留在这里当反面教材 ═══
        // 它当时直接拿 full.Row(…) 整行与 cut.Row(i) 比，而 shown 比带窄（放大 8 倍一屏只有
        // 50 列画布、带宽 60 列）⇒ 两根 span 长度不同，SequenceEqual 恒 false，
        // offByOne 与 fromZero 都等于总行数，看着像"两种错法都抓到了"，其实什么都没抓。
        // 那一版唯一的真信息是"主格当场红了"，判据的假设错了。现在把宽度钉死在这里：
        Battery.Assert(cut.Width == shown.Width && shown.Width < band.Width,
            $"夹具自证：这一格的窗口该是 {shown.Width} 宽（比带 {band.Width} 窄，所以横向也被裁）——" +
            "宽度一旦不再是被裁的量，上面那句「同宽同段」就要重写");
        Battery.Evidence($"对照图 {cut.Width}×{cut.Height}（带 {band.Width}×{band.Height}，列从第 {left} 起、行从第 {top} 起）：" +
                         $"同一矩形有 {same} 行对不上（正确读法一行不差），" +
                         $"错一行有 {diffAtShift} 行对不上、从第 0 行起有 {diffFromZero} 行对不上 " +
                         "⇒ 两种错法都会被同一套比较拒掉");

        static bool t1(RgbaImage img, int row, int leftCols, ReadOnlySpan<byte> c) =>
            img.Row(row).Slice(leftCols * 4, c.Length).SequenceEqual(c);
    }

    /// <summary>
    /// 取数那一笔要能报出"其中首次解码花了多少"。三件事各防一种假账：
    /// ① <b>内存源那一帧必须报 0 张／0ms</b>——这一头防"计数恒涨"（涨了它就又成了一笔说不清的账）；
    /// ② 文件源经 <c>Resolve</c> 首次解码必须报得出张数与毫秒（&gt;0），否则这句归因还是没量到；
    /// ③ 它是<b>取数的子项</b>：帧里读到的解码毫秒不许大于取数那一笔。
    ///    六笔加满整帧那个恒等式因此不受影响（第九改没动那六笔的边界）。
    /// </summary>
    private static void FetchStageReportsFirstDecodeInside()
    {
        using Harness h = Harness.New();
        h.Canvas.CreateControl();
        h.Render();
        StitchCanvasControl.StageNumbers n = h.Canvas.LastStageNumbersForTest
            ?? throw new InvalidOperationException("画完一帧没留下分阶段数值（结算没接上）");
        Battery.Assert(h.Canvas.LastPaintDecodeRunsForTest == 0 && h.Canvas.LastPaintDecodeMsForTest == 0.0,
            $"内存图源上首次解码读到 {h.Canvas.LastPaintDecodeRunsForTest} 张／" +
            $"{h.Canvas.LastPaintDecodeMsForTest:F3}ms：这条通路上根本没有解码这一步，" +
            "报出来就说明计数接错了地方（比如接在 Resolve 入口或接在别的线程上）");
        Battery.Assert(h.Canvas.LastPaintDecodeMsForTest <= n.Fetch + 1e-9,
            $"子项 {h.Canvas.LastPaintDecodeMsForTest:F3}ms 越过了父项（取数 {n.Fetch:F3}ms）——" +
            "那它就不是取数的一部分，六笔那本账也会跟着失真");

        var (dir, disk, id) = NewDiskResolverWithOnePng();
        try
        {
            long runs0 = FileSourceResolver.DecodeRunsTotalForTest;
            double tls0 = FileSourceResolver.DecodeMsOnCurrentThread;
            disk.Resolve(id);
            double firstMs = FileSourceResolver.DecodeMsOnCurrentThread - tls0;
            Battery.Assert(FileSourceResolver.DecodeRunsTotalForTest == runs0 + 1,
                $"首次解码张数没涨（{runs0} → {FileSourceResolver.DecodeRunsTotalForTest}）" +
                "⇒ 文件通路上真的解了一次，计数读不到就是没接上");
            Battery.Assert(firstMs > 0,
                "首次解码的毫秒读到 0 ⇒ 计时点没夹住那一次 Decode，" +
                "真窗口里那枚 45.4ms 的帧于是还是说不清是谁花的");
            disk.Resolve(id);      // 命中 LRU：这一次不该再解
            Battery.Assert(Math.Abs((FileSourceResolver.DecodeMsOnCurrentThread - tls0) - firstMs) < 1e-9,
                "第二次 Resolve（缓存命中）又给解码计时了 ⇒ 这一笔会把「要过几次」当成「解了几次」，" +
                "来回滚动时它会虚高，正好把要归因的那个现象编成假原因");
            Battery.Evidence($"内存源那一帧：解码 0 张／0ms（取数 {n.Fetch:F2}ms 里没有它）；" +
                             $"文件源首次解码 {firstMs:F3}ms／1 张，第二次命中缓存不再计时");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// 反证（精度红线 §3）：解码计数必须<b>只跟着真解码</b>走。最常见的写错法是接在
    /// <c>Resolve</c> 入口（那样缓存命中也涨），本条三头都判：
    /// ① 首次 Resolve 张数 +1、毫秒 &gt;0（没有这一句，下面两条"不涨"就成了恒真）；
    /// ② 同一张再 Resolve（命中 LRU）张数与毫秒都不许动；
    /// ③ <c>Invalidate</c> 之后再来一次<b>必须</b>重新算——它量的是"解了一次码"这件事，
    ///    不是"这张图被要过几次"。少了 ③，一个"只加一次就不再加"的实现也能全绿。
    /// </summary>
    private static void DecodeAttributionFollowsRealDecodesOnly()
    {
        var (dir, disk, id) = NewDiskResolverWithOnePng();
        try
        {
            long runs0 = FileSourceResolver.DecodeRunsTotalForTest;
            double tls0 = FileSourceResolver.DecodeMsOnCurrentThread;
            disk.Resolve(id);
            long runs1 = FileSourceResolver.DecodeRunsTotalForTest;
            double ms1 = FileSourceResolver.DecodeMsOnCurrentThread - tls0;
            Battery.Assert(runs1 == runs0 + 1 && ms1 > 0,
                $"夹具自证：首次必须报得出 1 张与正的毫秒（读到 {runs1 - runs0} 张／{ms1:F3}ms）" +
                "——不然下面的「不涨」是恒真");

            disk.Resolve(id);
            Battery.Assert(FileSourceResolver.DecodeRunsTotalForTest == runs1 &&
                           Math.Abs((FileSourceResolver.DecodeMsOnCurrentThread - tls0) - ms1) < 1e-9,
                "命中缓存那一次也计了数 ⇒ 计数接在 Resolve 入口而不是 Decode 上，" +
                "来回滚动会把「要过几次」报成「解了几次」");

            disk.Invalidate(id);
            disk.Resolve(id);
            Battery.Assert(FileSourceResolver.DecodeRunsTotalForTest == runs1 + 1,
                $"淘汰之后重新解码没被算进来（{runs1} → {FileSourceResolver.DecodeRunsTotalForTest}）" +
                "⇒ 这一笔只记第一次，等于把 LRU 淘汰的代价藏掉——那正是本族要量出来的东西");
            Battery.Evidence($"同一张图：首次 1 张／{ms1:F3}ms；命中缓存 +0；" +
                             $"Invalidate 后重来 +1 张（累计 {FileSourceResolver.DecodeRunsTotalForTest - runs0} 张）");
        }
        finally { Directory.Delete(dir, true); }
    }

    internal sealed class Harness : IDisposable
    {
        private readonly System.Drawing.Bitmap _frame = new(400, 500);
        public required StitchCanvasControl Canvas { get; init; }
        public required Project P { get; init; }
        /// <summary>夹具那份内存图源解析器：<c>SessionDiagnosticsTests</c> 里要故意整块合成一次，
        /// 得用<b>同一份</b>素材与同一个项目，另起一份就成了另一件事。</summary>
        public required MemoryResolver R { get; init; }
        public Seam Current => P.Doc.Seams[0];

        /// <summary>画一整帧（400×500）。控件没句柄时 WinForms 不发 WM_PAINT，所以必须留这道入口。</summary>
        public void Render() => Canvas.RenderFrameForTest(_frame);

        public void Dispose()
        {
            Canvas.Dispose();
            _frame.Dispose();
        }

        public static Harness New()
        {
            RgbaImage page = LongScrollTests.StripedPage(Width, Height + Height - Overlap);
            (RgbaImage before, RgbaImage after, int expected) =
                TestProject.CutScrollPair(page, firstTop: 0, firstHeight: Height, overlap: Overlap);
            Battery.AssertEqual(Overlap, expected, "夹具自证：切出来的重叠就是 120 行");
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, before, after);
            p.Mutate("设定重叠", _ =>
            {
                Seam seam = p.Doc.Seams[0];
                seam.AutoOffset = new IntPoint(0, 480);
                seam.SetOverlap(Overlap);
                seam.Analyzed = true;
            });
            var canvas = new StitchCanvasControl(p, new CanvasComposer(r)) { Size = new Size(400, 500) };
            canvas.Viewport.SetOneToOne();
            canvas.Viewport.MoveTopLeftTo(new IntPoint(0, 300));
            canvas.SelectSeam(p.Doc.Seams[0].Id);
            Battery.Assert(Overlap == p.Doc.Seams[0].OverlapLength, "夹具自证：这条缝真有重叠带");
            return new Harness { Canvas = canvas, P = p, R = r };
        }
    }
}
