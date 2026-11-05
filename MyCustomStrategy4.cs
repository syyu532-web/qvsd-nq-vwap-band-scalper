#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // [V4] V2(VwapBandAdjScalper2) 복사본. NQ 1계약용
    //   * 진입 로직(전략 1, 2)은 V2와 동일. 하단에서 사서/상단에서 팔고 VWAP에서 청산하는 1계약 단일 레그
    //   * UseLegB(기본 꺼짐): 끄면 B 레그(반대편 밴드 목표) 주문을 내지 않음 -> A 레그 1계약만, 익절 = VWAP
    //   * UseLongs / UseShorts(둘 다 기본 켜짐): 전략 1, 2 방향별 켜기/끄기
    //   * 전략 3(시가 통과) 기본 켜짐, OpenQuantity(기본 1) 계약
    //   * 클래스 이름 VwapBandAdjScalper4 (V2와 충돌 방지)
    //   * [BB 추세 필터] UseBbTrendFilter: 직전 마감 1분봉의 BB(2, 14)와 VWAP 비교 (전략 1, 2에 적용)
    //       - BB 하단 > VWAP = 강한 상승추세 -> 숏 금지. BB 하단이 VWAP 아래로 내려오면 다시 숏 허용
    //       - BB 상단 < VWAP = 강한 하락추세 -> 롱 금지. BB 상단이 VWAP 위로 올라오면 다시 롱 허용
    //   * [과열 진입] UseBbOverheatEntry: 추세가 지나쳐 과열이면 반대로 바로 진입 (HeatShort / HeatLong)
    //       - BB 하단 > VWAP 상단 밴드 = 상승 과열 -> 가격이 VWAP 상단 밴드에 닿는(이상인) 틱에 시장가 숏
    //       - BB 상단 < VWAP 하단 밴드 = 하락 과열 -> 가격이 VWAP 하단 밴드에 닿는(이하인) 틱에 시장가 롱
    //       - 밴드 워크/기울기/손익비/완익 후 BB 터치 조건 무시. 시간대, 포지션 1개, 완익 후 쿨다운은 적용
    //       - 익절 = VWAP (매 봉 갱신), 손절 = 전략 1 손절 설정, 계약 = LegAQuantity
    //       - 과열 구간 한 번에 방향별 1회만 진입 (과열이 풀리면 다시 가능)

    // [3전략 구성]
    //   [틱 진입] 전략 1, 2는 봉 마감을 기다리지 않음. 1틱 시리즈로 가격을 보다가 밴드 안으로 돌아오는 순간 시장가 진입.
    //            기준 밴드는 직전 마감 1분봉의 VWAP/밴드 (차트에 그려진 마지막 값). 방향별로 1분봉당 한 번만 판단
    //   전략 1) VWAP 밴드 터치 되돌림: 이번 1분봉에서 밴드 밖을 찍었다가 밴드 안으로 돌아오는 첫 틱에 시장가 진입
    //   전략 2) 밴드 이탈 마감 후 재진입: 종가가 밴드 밖에서 마감(크게 찢음)한 뒤, 나중에 가격이 다시 밴드에 닿으면 진입
    //           - 진입: 가격이 밴드 가격에 닿는 틱에 바로 시장가 (밴드가 움직여 못 잡으면 종가가 안으로 들어온 마감 시 시장가)
    //           - 3계약: A 2계약 VWAP 목표, B 1계약 반대편 밴드 목표 / SL = 진입 평단 ± ATR 기반 손절 (전략 2 설정, 기본 ATR×1.0, 20~30pt)
    //           - A 목표 체결 후 B 손절 이동은 전략 1과 동일 (MoveStopToBreakevenAtVwap)
    //
    //   전략 3) 9:30 ET 시가 통과 (틱 진입): 기준가 = 09:30:00 이후 첫 틱 가격(9:30 시가)
    //           - 09:30:00~09:30:59 ET는 시가만 기록하고 아무것도 안 함 (개장 직후 노이즈)
    //           - 09:31부터 가격이 시가 위/아래 어디에 있는지 출발 위치로 잡고, 그 뒤 시가를 통과하면 통과 방향으로 추세추종
    //             (위에서 아래로 통과 -> 숏, 아래에서 위로 통과 -> 롱, 통과하는 틱에 시장가)
    //           - OpenEnterOnFirstMove = true면 09:31 출발 위치 방향으로도 바로 진입 (시가 위 -> 롱, 아래 -> 숏)
    //           - 진입 09:31:00~09:39:59 ET, 하루 OpenMaxTrades회(기본 1), OpenQuantity 계약(기본 10)
    //           - 스캘핑: TP = 진입 평단 ± OpenTargetPoints(기본 10pt), SL = 진입 평단 ± OpenStopPoints(기본 10pt)
    //             둘 다 안 닿으면 09:40 ET 봉 마감에 시장가 청산
    //
    // [양방향 방지] 모든 진입은 CanEnter 검사를 거침: 포지션 플랫 + 반대 방향 진입 주문이 살아 있지 않을 때만 진입.
    //   롱 보유/롱 주문 대기 중에는 숏 주문을 내지 않고, 그 반대도 마찬가지
    //
    // [반대 방향 재진입 지연] 롱 청산 직후 숏(또는 숏 청산 직후 롱) 주문은 OppositeEntryDelaySeconds(기본 2초) 뒤에 냄.
    //   기다린 뒤 CanEnter를 다시 검사. 실시간에서만 동작 (백테스트는 봉 단위라 해당 없음)
    //
    // [완익 후 스위칭 필터 (볼린저 밴드)] UseBbSwitchFilter, 내장 Bollinger(BbStdDev=2, BbPeriod=14), 1분봉 기준, 차트 표시 없음
    //   * 완익 = VWAP 매매(전략 1, 2)의 모든 계약이 익절 주문(Profit target)으로 청산된 경우 (본절/손절/시간 청산이 섞이면 아님)
    //   * 완익 후 FullProfitCooldownMinutes(기본 2분) 동안은 방향 상관없이 모든 진입 금지
    //   * 쿨다운이 끝난 뒤 롱 완익 -> 다음 숏: 가격이 BB 상단에 "쿨다운 이후에 새로" 닿아야(틱 터치, 마감 불필요) 숏 진입 활성화
    //     숏 완익 -> 다음 롱: BB 하단 기준으로 같은 방식. 쿨다운 중 터치는 인정 안 함. 같은 방향 재진입은 BB 조건 없음
    //   * 다음 VWAP 매매가 시작되거나 날짜가 바뀌면 제한 해제
    //
    // [손익비 필터] UseRewardRiskFilter, MinRewardRisk(기본 1.0). 전략 1, 2의 모든 진입(틱 진입, 마감 대체 진입)에 적용
    //   * 손익비 = 진입가에서 A 목표(VWAP)까지 거리 ÷ 실제 손절 폭 (ATR 기반이면 그때 계산된 손절 폭)
    //   * 기준 미만이면 진입 안 함. 전략 3(시가 통과)은 익절 목표가 없어 적용 대상 아님
    //
    // [익절가 갱신] UpdateTargetsEachBar (기본 켜짐): VWAP 매매(전략 1, 2) 보유 중 1분봉 마감마다
    //   A 익절 = 그 봉의 VWAP, B 익절 = 그 봉의 반대편 밴드 끝으로 옮김 (진입 때 고정하지 않음)
    //   * 새 익절가가 진입 평단보다 1틱 이상 유리할 때만 옮기고, 아니면 직전 익절가 유지
    //   * A가 전부 익절된 뒤에는 B만 갱신
    //
    // [시간대]
    //   07:30 ET~ : 7:30 앵커 VWAP 계산/표시 시작 (밴드 준비 대기 로직 MinVwapSamples 삭제)
    //   08:30~09:30 ET, 09:45~10:30 ET : VWAP 전략(1, 2) 신규 진입
    //   09:30 ET : 남은 VWAP 포지션 정리 (FlatVwapAtOpen), 9:31부터 9:30 앵커 VWAP 사용
    //   09:30 ET : 시가만 기록, 09:31~09:40 ET 시가 통과 전략(3) 진입, 09:40 ET 봉 마감에 청산. 09:30~09:45 VWAP 전략 신규 진입 없음
    //   10:30 ET : 전체 청산
    //
    // [손절] 진입 평단 기준, 롱/숏 동일
    //   * 방향별로 따로 설정: UseAtrStops = 롱 (기본 꺼짐 = 고정), UseAtrStopsShort = 숏 (기본 켜짐 = ATR 기반)
    //   * ATR 기반: 손절 폭 = ATR(AtrPeriod, 1분봉, 직전 마감 봉) x 배수, 최소~최대 pt로 제한
    //       - 전략 1: x TouchStopAtrMultiple(1.0), 최소 TouchStopPoints(10pt) ~ 최대 TouchStopMaxPoints(25pt)
    //       - 전략 2: x ReentryStopAtrMultiple(1.0), 최소 ReentryStopPoints(20pt) ~ 최대 ReentryStopMaxPoints(30pt)
    //   * 고정: 전략 1 TouchStopPoints(10pt), 전략 2 ReentryStopPoints(20pt)
    //   * 전략 3(시가 통과)은 항상 OpenStopPoints(10pt) / OpenTargetPoints(10pt) 고정
    //
    // [V6k 변경점] V6h 기준 + 하단 밴드만 따로 늘리거나 줄이는 조절값 추가 (V6i/V6j의 롱 확인 로직은 모두 제거)
    //   * 하단 밴드 = VWAP - (기본 배수 + LowerBandOffset) x 표준편차
    //       - 기본 배수: 프리장(~9:30 ET) BandDeviations, 9:31 ET 이후 PostOpenBandDeviations
    //       - LowerBandOffsetPreOpen / LowerBandOffsetPostOpen: 구간별로 하단 밴드에만 더해지는 값 (+ = 넓힘, - = 좁힘, 기본 0)
    //       - 하단 밴드 최소 배수는 0.1로 제한
    //   * 조정된 하단 밴드는 롱 신호 기준선, 밴드 워크 필터, 숏 B 목표, 차트 표시에 사용
    //   * 숨겨진 대칭 하단 밴드는 삭제: 진입 신호, 필터, A/B 목표, 차트 모두 화면에 보이는 밴드 하나로 통일
    //   * 상단 밴드는 변경 없음
    //
    // [V6h] A 레그 2계약(VWAP 목표), B 레그 1계약(반대편 밴드 끝 목표, 전략 1/2 공통).
    //      A 목표가 전부 체결된 뒤에만 B 손절을 본절(B 진입 평단가)로 이동 (VWAP 체결가 부근 이동 옵션은 삭제)
    // [V6g] (삭제) B 목표 비율 TargetBFraction -> 이제 B 목표는 항상 반대편 밴드 끝
    // [V6f] 9:31 ET 이후 밴드 배수는 PostOpenBandDeviations, 프리장은 BandDeviations
    // [V6e] 프리장 앵커 VWAP을 코드에서 직접 계산 (현재 7:30 ET 앵커)
    //
    //  [V6d 내용]
    //  1) 5분 EMA 시각화: OnRender로 5분봉 EMA 점들을 이은 선
    //  2) 현재가 vs 5분 EMA 방향 필터 (토글): 1분 종가가 EMA 위면 롱만, 아래면 숏만 허용
    //  3) 본전/VWAP 스탑 (토글): A 레그 목표 체결 후 B 레그 손절 이동
    //
    //  [V6c 밴드 필터] (전략 1에만 적용)
    //  - 밴드 워크 필터: 종가가 밴드 밖에서 연속 N봉 마감하면 역방향 진입 금지
    //  - VWAP 기울기 필터: VWAP이 최근 SlopeBars봉 동안 봉당 MaxSlopeTicksPerBar틱 넘게 오르면 숏 금지, 내리면 롱 금지
    //  - 전략 2에도 VWAP 기울기 필터 별도 적용: UseReentrySlopeFilter / ReentryMaxSlopeTicksPerBar (SlopeBars는 공용)
    //  - Plot 0: VWAP (금색) / Plot 1: 상단 밴드 / Plot 2: 하단 밴드 / 5분 EMA는 OnRender
    //
    //  VWAP(9:30 이후)/EMA는 NinjaTrader 내장 지표 사용
    //  - VWAP: OrderFlowVWAP (Order Flow+ 필요). 9:31 ET 봉 이후 = PostOpenVwapHours 템플릿 (세션 시작 9:30 ET)
    //  - EMA: 5분 시리즈에 걸린 내장 EMA 값을 1분봉에서 읽어서 사용 (직전 마감 5분봉 기준)
    public class VwapBandAdjScalper4 : Strategy
    {
        // ---- 시간대 (ET, HHmmss, 1분봉 종료 시각 기준) ----
        private const int VwapCalcStart = 73000;       // 이 시각 이후 봉부터 7:30 앵커 VWAP 누적/표시
        private const int PreOpenTradeStart = 83000;   // VWAP 전략 프리장 진입 시작 (초과)
        private const int OpenTime = 93000;            // 개장. 이 시각까지 프리장 진입 (미만), 9:31 봉부터 9:30 앵커
        private const int OrbEntryStart = 93100;       // 시가 통과 전략 시작 (틱 시각, 이상). 9:30:00~9:30:59는 시가만 기록
        private const int OrbEntryEnd = 94000;         // 시가 통과 전략 진입 마감 (틱 시각, 미만)
        private const int OrbExitTime = 94000;         // 시가 통과 전략 청산 (이 봉 마감)
        private const int PostOpenTradeStart = 94500;  // VWAP 전략 재개 (이상)
        private const int LastEntryTime = 102900;      // 신규 진입 마감 (미만)
        private const int SessionEnd = 103000;         // 전체 청산

        private EMA trendEma;
        private ATR riskAtr;          // ATR 기반 손절용 (1분봉)
        private OrderFlowVWAP postOpenVwap;   // 9:30 ET 앵커
        private TimeZoneInfo easternZone;
        private bool trendReady;
        private int trendDirection;
        private double trendEmaValue;
        private DateTime trendTimestamp;
        private DateTime nyDay = DateTime.MinValue;
        private int entriesToday;
        private bool postOpenAnchorStarted;
        private int lastLongStopBar = -1000000;
        private int lastShortStopBar = -1000000;
        private int lastSignalBar = -1000000;
        private bool summaryPrinted;
        private int windowBars, longSignals, shortSignals;
        private HashSet<string> countedEntryOrders = new HashSet<string>();

        // ---- V6c ----
        private int barsAboveUpper, barsBelowLower;
        private Series<double> vwapSeries;   // 봉별 VWAP (기울기 계산용, 밴드 무효 봉은 NaN)
        private int vwapSamples;             // 현재 앵커(7:30 / 9:30) 이후 봉 수
        private int slopeBlockedLongs, slopeBlockedShorts;
        private int reentrySlopeBlockedLongs, reentrySlopeBlockedShorts;   // 전략 2 기울기 필터 차단 수
        private int walkBlockedShorts, walkBlockedLongs;

        // ---- V6d ----
        private List<DateTime> emaTimes = new List<DateTime>();   // 완성된 5분봉 시각 (그리기용)
        private List<double> emaValues = new List<double>();      // 그 5분봉의 EMA 값 (그리기용)
        private SharpDX.Direct2D1.Brush emaDx;
        private int sideBlockedLongs, sideBlockedShorts;
        private double legBFillValue;     // B 레그 체결가 x 수량 합 (평단가 계산용)
        private int legBFillQty;
        private bool breakevenMoved;
        private int activeLegAQuantity;
        private int legATargetFilledQty;
        private double legATargetFillValue;
        private string activeLegBName = "LongB";   // 손절 이동 대상 B 레그 신호 이름 (전략 1/2 공용)
        private string activeLegAName = "LongA";   // 익절 갱신 대상 A 레그 신호 이름 (전략 1/2 공용)
        private double activeTpA, activeTpB;       // 지금 걸려 있는 A/B 익절가 (갱신 판단용)

        // ---- V6e: 7:30 ET 앵커 VWAP 직접 누적 ----
        private double manualSumVolume, manualSumPriceVolume, manualSumSquareVolume;

        // ---- 전략 2: 밴드 밖 마감 후 재진입 ----
        private bool armedLong, armedShort;   // 종가가 하단 밖(롱 대기) / 상단 밖(숏 대기)에서 마감한 상태
        private int reentryEntries;

        // ---- 틱 진입 (전략 1, 2): 직전 마감 1분봉의 밴드/필터 값 + 지금 만들어지는 1분봉의 틱 고가/저가 ----
        private const int TickBip = 2;
        private bool ctxReady;
        private double ctxVwap, ctxUpper, ctxLower;
        private bool ctxSlopeReady;
        private double ctxSlopeTicks;   // 직전 마감 봉 기준 VWAP 기울기 (틱/봉)
        private double ctxAtr;          // 직전 마감 봉 ATR (손절 폭 계산용)
        private double tickBarHigh = double.MinValue, tickBarLow = double.MaxValue;
        private bool triedLongThisBar, triedShortThisBar;   // 방향별로 1분봉당 한 번만 진입 판단
        private bool heatLongUsed, heatShortUsed;           // V4 과열 진입: 과열 구간당 방향별 1회

        // ---- 완익 후 반대 방향 스위칭 필터 (볼린저 밴드, NinjaTrader 내장 Bollinger) ----
        private Bollinger bb;
        private double ctxBbUpper, ctxBbLower;              // 직전 마감 1분봉의 BB (틱 판단용)
        private bool bbHighSinceArmed, bbLowSinceArmed;     // 전략 2 대기 시작 후 BB 상단/하단을 뚫었는지
        private bool vwapTradeOpen, vwapTradeAllTargets;    // 진행 중인 VWAP 매매와, 지금까지 청산이 전부 익절 주문이었는지
        private MarketPosition vwapTradeSide = MarketPosition.Flat;
        private MarketPosition fullProfitSide = MarketPosition.Flat;   // 직전 VWAP 매매가 완익으로 끝난 방향 (Flat = 제한 없음)
        private bool bbBlockPrintedLong, bbBlockPrintedShort;
        private DateTime fullProfitTimeNy = DateTime.MinValue;   // 완익 청산 시각 (ET). 이후 FullProfitCooldownMinutes 동안 모든 진입 금지
        private bool bbHighAfterCooldown, bbLowAfterCooldown;    // 쿨다운이 끝난 뒤 BB 상단/하단에 새로 닿았는지 (완익 후 반대 진입 조건)
        private bool cooldownBlockPrinted;
        private DateTime lastExecutionTime = DateTime.MinValue;   // 마지막 체결 시각 (플랫폼 시간대)

        // ---- 양방향 방지: 아직 살아 있는(체결/취소/거부 전) 진입 주문 ----
        private List<Order> workingEntryOrders = new List<Order>();

        // ---- 반대 방향 재진입 지연: 롱 청산 직후 숏(또는 그 반대)은 OppositeEntryDelaySeconds 뒤에 주문 ----
        private MarketPosition lastPositionSide = MarketPosition.Flat;
        private MarketPosition lastClosedSide = MarketPosition.Flat;   // 마지막으로 청산된 포지션 방향
        private DateTime lastFlatTime = DateTime.MinValue;             // 그 청산 시각 (PC 시계)
        private System.Timers.Timer entryDelayTimer;
        private Action deferredEntry;
        private bool deferredIsLong;
        private string deferredTag;

        // ---- 전략 3: 9:30 ET 시가 통과 (틱) ----
        private bool orbActiveBar;    // 지금 만들어지는 1분봉이 9:30~9:40 구간인지 (틱마다 시간 변환 안 하도록)
        private bool orbRefSet;
        private double orbRef;        // 9:30 시가 (09:30:00 이후 첫 틱)
        private int orbLastSide;      // 기준가 대비 마지막 위치: +1 위, -1 아래, 0 아직 출발 전
        private int orbTradesToday;

        // ---- 9:30 ET VWAP 포지션 정리 (하루 1회) ----
        private bool openFlatDone;


        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "VwapBandAdjScalper4";
                Description = "V4 (NQ, 1 contract, band -> VWAP, both directions). Based on V2. 3 strategies: (1) VWAP band touch-and-return reversal (ATR stop 10-25pt), (2) re-entry at the band after a close outside it (ATR stop 20-30pt, A=VWAP, B=opposite band), (3) 9:30 ET open-price cross (1 contract, 10pt stop, exit 9:40). VWAP from 7:30 ET; VWAP trades 8:30-9:30 and 9:45-10:30 ET.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                EntriesPerDirection = 2;
                EntryHandling = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                LegQuantity = 1;       // B 레그 계약 수 (UseLegB 켤 때만 사용)
                LegAQuantity = 1;      // A 레그 계약 수: VWAP 목표
                UseLegB = false;       // V4: B 레그 안 씀 -> 1계약, VWAP 익절만
                UseLongs = true;       // V4: 하단 밴드 롱
                UseShorts = true;      // V4: 상단 밴드 숏도 진입
                UseBbTrendFilter = true;     // V4: BB 하단 > VWAP 숏 금지 / BB 상단 < VWAP 롱 금지
                UseBbOverheatEntry = true;   // V4: BB 하단 > VWAP 상단 밴드 -> 상단 밴드 터치 숏 (롱은 반대)
                EmaPeriod = 20;
                // 손절 (전략 1, 2): 롱은 고정 pt (전략 1 = TouchStopPoints 10, 전략 2 = ReentryStopPoints 20), 숏은 ATR 기반
                UseAtrStops = false;        // 롱
                UseAtrStopsShort = true;    // 숏
                AtrPeriod = 10;
                TouchStopPoints = 10.0;        // 전략 1: 고정 손절 (ATR 켜면 최소값)
                TouchStopAtrMultiple = 1.0;
                TouchStopMaxPoints = 25.0;
                CooldownBars = 0;
                BandDeviations = 0.8;
                MaxEntriesPerDay = 0;
                UseTrendFilter = false;
                DebugOutput = true;

                // V6d 토글
                UsePriceSideFilter = false;
                MoveStopToBreakevenAtVwap = true;

                // 9:30 ET 이후 밴드 배수
                PostOpenBandDeviations = 1.2;

                // V6k: 하단 밴드만 따로 조정 (SD 배수에 더해짐: + 넓힘, - 좁힘)
                LowerBandOffsetPreOpen = 0.0;
                LowerBandOffsetPostOpen = 0.0;

                // 9:30 앵커 VWAP용 Trading Hours 템플릿 이름
                PostOpenVwapHours = "CME US Index Futures RTH"; // 기본 템플릿 (세션 시작 9:30 ET)

                // V6c 필터 기본값
                UseBandWalkFilter = true;
                WalkBars = 1;
                UseVwapSlopeFilter = true;
                SlopeBars = 10;
                MaxSlopeTicksPerBar = 1.0;
                UseReentrySlopeFilter = true;      // 전략 2 기울기 필터 (전략 1과 별도)
                ReentryMaxSlopeTicksPerBar = 1.0;

                // 전략 2: 밴드 밖 마감 후 재진입
                UseBandReentry = true;
                ReentryStopPoints = 20.0;      // 전략 2: 고정 손절 (ATR 켜면 최소값)
                ReentryStopAtrMultiple = 1.0;
                ReentryStopMaxPoints = 30.0;

                // 9:30 ET에 남은 VWAP 포지션 정리
                FlatVwapAtOpen = true;

                // 전략 3: 9:30 시가 통과
                UseOpeningDrive = true;    // V4: 켜짐 (1계약)
                OpenQuantity = 1;
                OpenStopPoints = 10.0;
                OpenTargetPoints = 10.0;
                OpenMaxTrades = 1;
                OpenEnterOnFirstMove = false;

                // 반대 방향 재진입 지연 (실시간만)
                OppositeEntryDelaySeconds = 2.0;

                // 완익 후 반대 방향 진입은 볼린저 밴드도 뚫고 들어와야 함 (차트 표시는 지표에서 직접 추가)
                UseBbSwitchFilter = true;
                BbPeriod = 14;
                BbStdDev = 2.0;
                FullProfitCooldownMinutes = 2.0;   // 완익 후 모든 진입 금지 시간 (분)

                // 손익비 필터: VWAP(A 목표)까지 거리가 손절 폭의 MinRewardRisk배 이상일 때만 진입
                UseRewardRiskFilter = true;
                MinRewardRisk = 1.0;

                // 보유 중 익절가를 매 봉 최신 VWAP(A) / 반대편 밴드(B)로 갱신
                UpdateTargetsEachBar = true;

                ShowBands = true;
                ShowEma = true;
                EmaBrush = Brushes.DeepSkyBlue;
                EmaWidth = 2;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);   // BarsInProgress 1: 5분 EMA
                AddDataSeries(BarsPeriodType.Tick, 1);     // BarsInProgress 2 (TickBip): 봉 마감 전 틱 진입 판단용

                AddPlot(new Stroke(Brushes.Gold, DashStyleHelper.Solid, 2), PlotStyle.Line, "VWAP");
                AddPlot(new Stroke(Brushes.OrangeRed, DashStyleHelper.Solid, 1), PlotStyle.Line, "UpperBand");
                AddPlot(new Stroke(Brushes.LimeGreen, DashStyleHelper.Solid, 1), PlotStyle.Line, "LowerBand");
            }
            else if (State == State.DataLoaded)
            {
                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
                    throw new InvalidOperationException("Use this strategy on a 1-minute primary series.");
                string root = Instrument.MasterInstrument.Name.ToUpperInvariant();
                if (root != "NQ" && root != "MNQ" && root != "GC" && root != "MGC")
                    throw new InvalidOperationException("Allowed roots: NQ, MNQ, GC, MGC.");
                trendEma = EMA(Closes[1], EmaPeriod);
                riskAtr = ATR(AtrPeriod);
                bb = Bollinger(BbStdDev, BbPeriod);   // 1분봉 기준, 계산만 하고 차트에는 그리지 않음

                postOpenVwap = OrderFlowVWAP(BarsArray[0], VWAPResolution.Standard, LoadTradingHours(PostOpenVwapHours),
                    VWAPStandardDeviations.Two, 1.0, BandDeviations, BandDeviations);

                emaTimes.Clear();
                emaValues.Clear();

                easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                vwapSeries = new Series<double>(this);

                entryDelayTimer = new System.Timers.Timer();
                entryDelayTimer.AutoReset = false;
                entryDelayTimer.Elapsed += OnEntryDelayElapsed;
            }
            else if (State == State.Terminated)
            {
                if (entryDelayTimer != null)
                {
                    entryDelayTimer.Stop();
                    entryDelayTimer.Elapsed -= OnEntryDelayElapsed;
                    entryDelayTimer.Dispose();
                    entryDelayTimer = null;
                }
                deferredEntry = null;
                DisposeDxBrushes();
            }
        }

        private static TradingHours LoadTradingHours(string templateName)
        {
            TradingHours hours = TradingHours.String2TradingHours(templateName);
            if (hours == null)
                throw new InvalidOperationException(string.Format(
                    "Trading Hours template '{0}' not found. Check Tools > Trading Hours (session start 9:30 AM Eastern) or fix the name in the VWAP settings.",
                    templateName));
            return hours;
        }

        private DateTime NewYorkTime(DateTime timestamp)
        {
            DateTime unspecified = DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(unspecified,
                NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, easternZone);
        }

        private void ClearBandPlots()
        {
            Values[0].Reset();
            Values[1].Reset();
            Values[2].Reset();
        }

        private void ResetLegBTracking()
        {
            legBFillValue = 0;
            legBFillQty = 0;
            breakevenMoved = false;
            activeLegAQuantity = 0;
            legATargetFilledQty = 0;
            legATargetFillValue = 0;
        }

        // 전략 1(LongA/LongB) + 전략 2(RevLongA/RevLongB) 공용 A/B 레그 이름 판별
        private static bool IsLegAEntry(string name)
        {
            return name == "LongA" || name == "ShortA" || name == "RevLongA" || name == "RevShortA"
                || name == "HeatLong" || name == "HeatShort";
        }

        private static bool IsLegBEntry(string name)
        {
            return name == "LongB" || name == "ShortB" || name == "RevLongB" || name == "RevShortB";
        }

        private static bool IsOpeningDriveEntry(string name)
        {
            return name == "OrbLong" || name == "OrbShort";
        }

        private static bool IsEntryName(string name)
        {
            return IsLegAEntry(name) || IsLegBEntry(name) || IsOpeningDriveEntry(name);
        }

        // 양방향 방지: 모든 전략(1, 2, 3)의 진입은 이 검사를 통과해야만 주문을 낸다.
        //  - 포지션이 플랫이어야 함 (롱 보유 중 숏 진입 / 숏 보유 중 롱 진입 불가)
        //  - 반대 방향 진입 주문(체결 대기 중인 시장가 포함)이 하나라도 살아 있으면 불가
        // 완익 청산 후 FullProfitCooldownMinutes가 아직 안 지났는지 (ny = 지금 판단 시각, ET)
        private bool InFullProfitCooldown(DateTime ny)
        {
            return FullProfitCooldownMinutes > 0 && fullProfitTimeNy != DateTime.MinValue
                && (ny - fullProfitTimeNy).TotalMinutes < FullProfitCooldownMinutes;
        }

        private bool CanEnter(bool isLong, DateTime ny, string tag)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return false;
            // 완익 후 쿨다운: 방향/전략 상관없이 진입 금지
            if (InFullProfitCooldown(ny))
            {
                if (DebugOutput && !cooldownBlockPrinted)
                {
                    cooldownBlockPrinted = true;
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET BLOCKED {1} {2}: full-profit cooldown until {3:HH:mm:ss} ET",
                        ny, tag, isLong ? "LONG" : "SHORT", fullProfitTimeNy.AddMinutes(FullProfitCooldownMinutes)));
                }
                return false;
            }
            // 상태 알림을 놓친 주문이 계속 막지 않도록 이미 끝난 주문은 정리
            workingEntryOrders.RemoveAll(x => x.OrderState == OrderState.Filled
                || x.OrderState == OrderState.Cancelled || x.OrderState == OrderState.Rejected);
            foreach (Order o in workingEntryOrders)
            {
                bool orderIsLong = o.OrderAction == OrderAction.Buy;
                if (orderIsLong != isLong)
                {
                    if (DebugOutput)
                        Print(string.Format("{0:yyyy-MM-dd HH:mm} ET BLOCKED {1} {2}: opposite entry order '{3}' still {4}",
                            ny, tag, isLong ? "LONG" : "SHORT", o.Name, o.OrderState));
                    return false;
                }
            }
            return true;
        }

        // 반대 방향 재진입 지연: 마지막 청산이 반대 방향이고 OppositeEntryDelaySeconds가 아직 안 지났으면
        // 주문을 바로 내지 않고 남은 시간만큼 기다렸다가 다시 CanEnter 검사 후 주문한다.
        // (같은 방향 재진입은 바로 주문. 백테스트/히스토리는 봉 단위라 지연 없음)
        private void SubmitOrDefer(bool isLong, DateTime ny, string tag, Action submit)
        {
            bool oppositeOfLastClose = isLong ? lastClosedSide == MarketPosition.Short
                                              : lastClosedSide == MarketPosition.Long;
            double waitSeconds = OppositeEntryDelaySeconds - (DateTime.Now - lastFlatTime).TotalSeconds;
            if (State != State.Realtime || !oppositeOfLastClose || waitSeconds <= 0 || entryDelayTimer == null)
            {
                submit();
                return;
            }

            deferredEntry = submit;
            deferredIsLong = isLong;
            deferredTag = tag;
            entryDelayTimer.Stop();
            entryDelayTimer.Interval = Math.Max(50.0, waitSeconds * 1000.0);
            entryDelayTimer.Start();
            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET DELAY {1} {2}: last {3} closed {4:F1}s ago -> sending in {5:F1}s",
                    ny, tag, isLong ? "LONG" : "SHORT", lastClosedSide,
                    (DateTime.Now - lastFlatTime).TotalSeconds, waitSeconds));
        }

        private void OnEntryDelayElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (State != State.Realtime)
                return;
            TriggerCustomEvent(o => RunDeferredEntry(), 0, null);
        }

        private void RunDeferredEntry()
        {
            Action entry = deferredEntry;
            deferredEntry = null;
            if (entry == null)
                return;
            // 기다리는 동안 포지션/주문 상태가 바뀌었을 수 있으므로 양방향 검사를 다시 함
            if (!CanEnter(deferredIsLong, NewYorkTime(Time[0]), "DELAYED " + deferredTag))
                return;
            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET DELAYED {1} {2} sent",
                    NewYorkTime(Time[0]), deferredTag, deferredIsLong ? "LONG" : "SHORT"));
            entry();
        }

        // 세션 구간과 무관하게 A 레그 LegAQuantity(VWAP 목표), B 레그 LegQuantity(반대편 밴드 끝 목표)를 사용한다.
        private void GetEntryQuantities(out int qtyA, out int qtyB)
        {
            qtyA = Math.Max(1, LegAQuantity);
            qtyB = Math.Max(1, LegQuantity);
            if (DebugOutput)
                Print(string.Format("ENTRY QTY: A(VWAP)={0} B(opposite band)={1}", qtyA, qtyB));
        }

        private double PointsToTicks(double points)
        {
            return Math.Max(1, Math.Round(points / TickSize));
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                if (CurrentBars[1] < EmaPeriod - 1)
                    return;
                trendEmaValue = trendEma[0];
                trendDirection = Closes[1][0] > trendEmaValue ? 1 :
                                 Closes[1][0] < trendEmaValue ? -1 : 0;
                trendTimestamp = Times[1][0];
                trendReady = true;

                // 그리기용: 5분봉 종료 시각과 EMA 값 기록
                emaTimes.Add(Times[1][0]);
                emaValues.Add(trendEmaValue);
                return;
            }
            if (BarsInProgress == TickBip)
            {
                OnIntrabarTick();
                return;
            }
            if (BarsInProgress != 0)
                return;

            // 기본값 NaN, 밴드가 유효한 봉에서만 아래에서 VWAP 값으로 덮어씀 (기울기 계산용)
            vwapSeries[0] = double.NaN;

            // 새 1분봉 시작: 틱 고가/저가와 봉당 1회 판단 플래그 초기화.
            // 틱 진입 기준값(ctx)은 아래에서 이번 봉 밴드가 유효할 때만 다시 채움
            tickBarHigh = double.MinValue;
            tickBarLow = double.MaxValue;
            triedLongThisBar = triedShortThisBar = false;
            bbBlockPrintedLong = bbBlockPrintedShort = false;
            ctxReady = false;

            DateTime ny = NewYorkTime(Time[0]);
            int hhmmss = ToTime(ny);
            // 이번 봉 마감 시각이 9:29~9:39이면 다음 1분 동안 시가 통과 전략 틱 판단
            orbActiveBar = UseOpeningDrive && hhmmss >= 92900 && hhmmss < OrbEntryEnd;
            if (ny.Date != nyDay)
            {
                nyDay = ny.Date;
                entriesToday = 0;
                vwapSamples = 0;
                slopeBlockedLongs = slopeBlockedShorts = 0;
                reentrySlopeBlockedLongs = reentrySlopeBlockedShorts = 0;
                countedEntryOrders.Clear();
                postOpenAnchorStarted = false;
                windowBars = longSignals = shortSignals = 0;
                barsAboveUpper = barsBelowLower = 0;
                walkBlockedShorts = walkBlockedLongs = 0;
                sideBlockedLongs = sideBlockedShorts = 0;
                manualSumVolume = manualSumPriceVolume = manualSumSquareVolume = 0;
                summaryPrinted = false;
                armedLong = armedShort = false;
                reentryEntries = 0;
                openFlatDone = false;
                orbRefSet = false;
                orbRef = 0;
                orbLastSide = 0;
                orbTradesToday = 0;
                fullProfitSide = MarketPosition.Flat;   // 완익 스위칭 제한은 하루 단위
                fullProfitTimeNy = DateTime.MinValue;
                bbHighAfterCooldown = bbLowAfterCooldown = false;
                heatLongUsed = heatShortUsed = false;
            }

            if (hhmmss >= SessionEnd && !summaryPrinted)
            {
                summaryPrinted = true;
                if (DebugOutput)
                {
                    Print(string.Format("{0:yyyy-MM-dd} {1} ET windowBars={2} longSignals={3} shortSignals={4} entries={5} (reentry={10}) openDrive={11} | blocked: walk(S/L)={6}/{7} emaSide(S/L)={8}/{9} slope(S/L)={12}/{13} reentrySlope(S/L)={14}/{15}",
                        nyDay, Instrument.FullName, windowBars, longSignals, shortSignals, entriesToday,
                        walkBlockedShorts, walkBlockedLongs, sideBlockedShorts, sideBlockedLongs, reentryEntries, orbTradesToday, slopeBlockedShorts, slopeBlockedLongs,
                        reentrySlopeBlockedShorts, reentrySlopeBlockedLongs));
                }
            }

            bool inWindow = hhmmss > VwapCalcStart && hhmmss < SessionEnd;
            if (!inWindow)
            {
                ClearBandPlots();
                barsAboveUpper = barsBelowLower = 0;
                armedLong = armedShort = false;
                deferredEntry = null;   // 매매 시간 밖에서는 대기 중인 지연 주문도 버림
                if (hhmmss >= SessionEnd)
                {
                    // 전략 1/2/3 모든 진입 청산
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong();
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort();
                }
                return;
            }

            windowBars++;

            // ---- VWAP 앵커 전환: 9:30 이후 첫 봉(9:31)부터 9:30 앵커 VWAP 사용 ----
            if (hhmmss > OpenTime && !postOpenAnchorStarted)
            {
                barsAboveUpper = barsBelowLower = 0;
                vwapSamples = 0;                  // 기울기는 새 앵커 VWAP이 SlopeBars봉 쌓인 뒤부터 계산
                armedLong = armedShort = false;   // 이전 앵커 밴드 기준의 이탈 상태는 무효
                postOpenAnchorStarted = true;
            }
            if (Volume[0] > 0)
                vwapSamples++;

            // ---- V6e: 7:30 ET 앵커 VWAP 직접 누적 (07:30 초과 봉부터, 9:30 이후에는 사용하지 않음) ----
            if (!postOpenAnchorStarted)
            {
                double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
                double barVolume = Math.Max(0.0, (double)Volume[0]);
                if (barVolume > 0)
                {
                    manualSumVolume += barVolume;
                    manualSumPriceVolume += barVolume * typicalPrice;
                    manualSumSquareVolume += barVolume * typicalPrice * typicalPrice;
                }
            }

            // ---- VWAP / 밴드 (게이트보다 먼저 읽어서 포지션 중에도 차트에 표시) ----
            bool vwapValid;
            bool bandsReady = false;
            double vwap = 0, sd = 0, lower = 0, upper = 0;
            if (!postOpenAnchorStarted)
            {
                // 7:31 ~ 9:30 ET: 코드에서 직접 계산한 7:30 앵커 VWAP
                vwapValid = manualSumVolume > 0;
                if (vwapValid)
                {
                    vwap = manualSumPriceVolume / manualSumVolume;
                    double variance = Math.Max(0.0, manualSumSquareVolume / manualSumVolume - vwap * vwap);
                    sd = Math.Sqrt(variance);
                    if (sd >= TickSize)
                    {
                        lower = vwap - BandDeviations * sd;
                        upper = vwap + BandDeviations * sd;
                        bandsReady = true;
                    }
                }
            }
            else
            {
                // 9:31 ET 이후: 내장 OrderFlowVWAP(9:30 앵커) 값 사용
                vwapValid = postOpenVwap.VWAP.IsValidDataPoint(0) && postOpenVwap.VWAP[0] > 0;
                if (vwapValid)
                {
                    vwap = postOpenVwap.VWAP[0];
                    sd = Math.Max(0.0, postOpenVwap.StdDev1Upper[0] - vwap);   // SD1 배수 = 1 → 원시 sd
                    if (sd >= TickSize)
                    {
                        upper = vwap + PostOpenBandDeviations * sd;
                        lower = vwap - PostOpenBandDeviations * sd;
                        bandsReady = true;
                    }
                }
            }

            // ---- V6k: 하단 밴드만 따로 늘리거나 줄임. 이후 모든 계산(신호, 필터, 목표, 차트)은 이 조정된 밴드를 사용 ----
            if (bandsReady)
            {
                double baseMultiple = postOpenAnchorStarted ? PostOpenBandDeviations : BandDeviations;
                double lowerOffset = postOpenAnchorStarted ? LowerBandOffsetPostOpen : LowerBandOffsetPreOpen;
                double lowerMultiple = Math.Max(0.1, baseMultiple + lowerOffset);
                lower = vwap - lowerMultiple * sd;
            }

            // 진단: 매일 9:00 / 10:00 ET에 전략이 읽은 VWAP·EMA 값을 그대로 출력
            if (DebugOutput && (hhmmss == 90000 || hhmmss == 100000))
                Print(string.Format("{0:yyyy-MM-dd HH:mm} ET SNAPSHOT {1} valid={2} vwap={3:F2} sd={4:F2} band={5:F2}/{6:F2} close={7:F2} trendReady={8} trend={9} ema5m={10:F2} ema5mTime={11:HH:mm}",
                    ny, postOpenAnchorStarted ? "post(9:30 built-in)" : "pre(7:30 manual)",
                    vwapValid, vwap, sd, lower, upper,
                    Close[0], trendReady, trendDirection, trendEmaValue, trendTimestamp));

            // ---- V6c: 밴드 워크 카운터 + VWAP 기울기 (포지션 중에도 매 봉 갱신) ----
            double slopeTicks = 0;
            bool slopeReady = false;
            if (bandsReady)
            {
                vwapSeries[0] = vwap;
                barsAboveUpper = Close[0] > upper ? barsAboveUpper + 1 : 0;
                barsBelowLower = Close[0] < lower ? barsBelowLower + 1 : 0;

                // 현재 앵커 이후 SlopeBars봉 이상 쌓였을 때만: (지금 VWAP - SlopeBars봉 전 VWAP) / 틱 / SlopeBars
                if ((UseVwapSlopeFilter || UseReentrySlopeFilter) && vwapSamples > SlopeBars && CurrentBar >= SlopeBars
                    && !double.IsNaN(vwapSeries[SlopeBars]))
                {
                    slopeTicks = (vwap - vwapSeries[SlopeBars]) / TickSize / SlopeBars;
                    slopeReady = true;
                }
            }
            else
            {
                barsAboveUpper = barsBelowLower = 0;
            }

            if (ShowBands && bandsReady)
            {
                Values[0][0] = vwap;
                Values[1][0] = upper;
                Values[2][0] = lower;
            }
            else
                ClearBandPlots();

            // ---- VWAP 매매 보유 중: 익절가를 이번 봉의 VWAP / 반대편 밴드로 갱신 ----
            if (bandsReady)
                UpdateVwapTargets(vwap, upper, lower, ny);

            // ---- 9:30 ET: 남은 VWAP 포지션 정리 ----
            FlatVwapPositionAtOpen(ny, hhmmss);

            // ---- 전략 3: 9:40 ET 봉 마감에 시가 통과 포지션 청산 (fromEntrySignal 지정이라 다른 진입은 안 건드림) ----
            if (hhmmss >= OrbExitTime && hhmmss < PostOpenTradeStart)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("OrbExitL", "OrbLong");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("OrbExitS", "OrbShort");
            }

            // ---- 전략 2: 밴드 밖 마감 상태 추적 (포지션 중에도 매 봉 갱신) ----
            //  종가가 하단 밖 -> 롱 대기, 상단 밖 -> 숏 대기.
            //  대기 중에는 틱마다 가격이 밴드에 닿는지 확인해 바로 시장가 진입 (OnIntrabarTick).
            //  틱으로 못 잡은 채 종가가 밴드 안으로 들어오면(밴드가 움직인 경우) 마감 즉시 시장가 진입
            bool reentryAtCloseLong = false, reentryAtCloseShort = false;
            if (UseBandReentry && bandsReady)
            {
                if (Close[0] < lower)
                {
                    if (!armedLong)
                    {
                        bbLowSinceArmed = false;   // 새 대기 시작: BB 하단 돌파 여부 새로 기록
                        if (DebugOutput)
                            Print(string.Format("{0:yyyy-MM-dd HH:mm} ET REENTRY ARMED LONG: close={1} < lower={2:F2}", ny, Close[0], lower));
                    }
                    armedLong = true;
                    armedShort = false;
                    if (Low[0] < bb.Lower[0])
                        bbLowSinceArmed = true;
                }
                else if (Close[0] > upper)
                {
                    if (!armedShort)
                    {
                        bbHighSinceArmed = false;
                        if (DebugOutput)
                            Print(string.Format("{0:yyyy-MM-dd HH:mm} ET REENTRY ARMED SHORT: close={1} > upper={2:F2}", ny, Close[0], upper));
                    }
                    armedShort = true;
                    armedLong = false;
                    if (High[0] > bb.Upper[0])
                        bbHighSinceArmed = true;
                }
                else
                {
                    reentryAtCloseLong = armedLong;
                    reentryAtCloseShort = armedShort;
                    armedLong = armedShort = false;
                }
            }
            else
                armedLong = armedShort = false;

            // ---- 다음 1분 동안 틱 진입 판단에 쓸 기준값 = 이번 마감 봉의 밴드/필터 (차트에 그려진 마지막 값) ----
            ctxReady = bandsReady;
            ctxVwap = vwap;
            ctxUpper = upper;
            ctxLower = lower;
            ctxSlopeReady = slopeReady;
            ctxSlopeTicks = slopeTicks;
            ctxAtr = riskAtr[0];
            ctxBbUpper = bb.Upper[0];
            ctxBbLower = bb.Lower[0];
            // 과열이 풀리면 그 방향 과열 진입 다시 허용
            if (!bandsReady || !(ctxBbLower > ctxUpper)) heatShortUsed = false;
            if (!bandsReady || !(ctxBbUpper < ctxLower)) heatLongUsed = false;

            // ---- 전략 2 마감 대체 진입 ----
            //  대기 중 밴드가 움직여 틱 진입이 안 된 채 종가가 새 밴드 안으로 들어온 경우 -> 마감 즉시 시장가
            //  매매 시간 판단은 봉 시작 시각 기준: 08:29~08:30 봉(마감 08:30)처럼 매매 시간 전에 들어온 움직임으로는 진입 안 함
            int barStartHhmmss = ToTime(ny.AddMinutes(-1));
            if (!(reentryAtCloseLong || reentryAtCloseShort) || !bandsReady || !IsVwapTradeTime(barStartHhmmss))
                return;
            if (CurrentBar <= lastSignalBar || !VwapEntryGatesOpen())
                return;
            bool fallbackLong = reentryAtCloseLong;
            if (!BbSwitchOk(fallbackLong, Close[0], bb.Upper[0], bb.Lower[0], ny))
                return;
            TryBandReentry(fallbackLong, Close[0], ny, "MARKET(closed back inside)");
        }

        // 완익 후 반대 방향 스위칭 필터:
        //  직전 VWAP 매매가 롱 완익이면 다음 숏은 쿨다운이 끝난 뒤 BB 상단에 한 번이라도 닿아야(터치) 활성화
        //  (숏 완익이면 롱은 BB 하단). 활성화 뒤에는 원래 전략 신호대로 진입.
        //  같은 방향 재진입이나 직전 매매가 완익이 아니면 조건 없음. 막히면 방향별로 봉당 한 번만 로그
        private bool BbSwitchOk(bool isLong, double price, double bbUpper, double bbLower, DateTime ny)
        {
            if (!UseBbSwitchFilter || fullProfitSide != (isLong ? MarketPosition.Short : MarketPosition.Long))
                return true;
            bool pierced = isLong ? bbLowAfterCooldown : bbHighAfterCooldown;
            if (pierced)
                return true;
            if (DebugOutput && !(isLong ? bbBlockPrintedLong : bbBlockPrintedShort))
            {
                if (isLong) bbBlockPrintedLong = true; else bbBlockPrintedShort = true;
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET BLOCKED {1} by BB-SWITCH (after {2} full profit): pierced={3} price={4} bb={5:F2}",
                    ny, isLong ? "LONG" : "SHORT", fullProfitSide, pierced, price, isLong ? bbLower : bbUpper));
            }
            return false;
        }

        // 익절가 갱신: VWAP 매매(전략 1, 2) 보유 중 봉 마감마다 A = 최신 VWAP, B = 최신 반대편 밴드 끝.
        //  진입 평단보다 1틱 이상 유리한 가격일 때만 옮김 (VWAP이 진입가 쪽으로 넘어오면 직전 익절가 유지)
        private void UpdateVwapTargets(double vwap, double upper, double lower, DateTime ny)
        {
            if (!UpdateTargetsEachBar || !vwapTradeOpen || Position.MarketPosition == MarketPosition.Flat)
                return;
            bool isLong = Position.MarketPosition == MarketPosition.Long;
            double avg = Position.AveragePrice;
            double newA = Instrument.MasterInstrument.RoundToTickSize(vwap);
            double newB = Instrument.MasterInstrument.RoundToTickSize(isLong ? upper : lower);

            bool legAOpen = activeLegAQuantity > 0 && legATargetFilledQty < activeLegAQuantity;
            if (legAOpen && newA != activeTpA && (isLong ? newA >= avg + TickSize : newA <= avg - TickSize))
            {
                SetProfitTarget(activeLegAName, CalculationMode.Price, newA);
                if (DebugOutput)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} ET TARGET A {1} -> {2} (latest VWAP)", ny, activeTpA, newA));
                activeTpA = newA;
            }
            if (UseLegB && newB != activeTpB && (isLong ? newB >= avg + TickSize : newB <= avg - TickSize))
            {
                SetProfitTarget(activeLegBName, CalculationMode.Price, newB);
                if (DebugOutput)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm} ET TARGET B {1} -> {2} (latest opposite band)", ny, activeTpB, newB));
                activeTpB = newB;
            }
        }

        // 손절 폭(pt): 방향별 설정이 ATR 기반이면 직전 마감 봉 ATR x 배수를 최소~최대로 제한, 아니면 고정값(minPoints)
        private double StopDistance(bool isLong, double minPoints, double atrMultiple, double maxPoints)
        {
            if (!(isLong ? UseAtrStops : UseAtrStopsShort) || double.IsNaN(ctxAtr) || ctxAtr <= 0)
                return minPoints;
            double pts = Math.Max(minPoints, Math.Min(Math.Max(minPoints, maxPoints), ctxAtr * atrMultiple));
            return Instrument.MasterInstrument.RoundToTickSize(pts);
        }

        // 손익비 필터 (전략 1, 2 모든 진입): A 목표(VWAP)까지 거리 ÷ 손절 폭이 MinRewardRisk 미만이면 진입 안 함
        private bool RewardRiskOk(bool isLong, double price, double tpA, double stopPoints, DateTime ny, string tag)
        {
            if (!UseRewardRiskFilter || stopPoints <= 0)
                return true;
            double reward = isLong ? tpA - price : price - tpA;
            double rr = reward / stopPoints;
            if (rr >= MinRewardRisk)
                return true;
            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET BLOCKED {1} {2} by R:R {3:F2} < {4:F2} (to VWAP {5:F2}pt, stop {6:F2}pt) price={7} vwap={8}",
                    ny, tag, isLong ? "LONG" : "SHORT", rr, MinRewardRisk, reward, stopPoints, price, tpA));
            return false;
        }

        // VWAP 전략(1, 2) 신규 진입 시간: 08:30~09:30 ET, 09:45~10:29 ET (09:30~09:45 없음)
        private static bool IsVwapTradeTime(int hhmmss)
        {
            return (hhmmss >= PreOpenTradeStart && hhmmss < OpenTime)
                || (hhmmss >= PostOpenTradeStart && hhmmss < LastEntryTime);
        }

        // VWAP 전략(1, 2) 공통 진입 조건: 플랫, 지연 주문 대기 없음, 최소 봉 수, 하루 최대 진입 수
        private bool VwapEntryGatesOpen()
        {
            if (Position.MarketPosition != MarketPosition.Flat || deferredEntry != null)
                return false;
            if (CurrentBars[0] < BarsRequiredToTrade)
                return false;
            return MaxEntriesPerDay <= 0 || entriesToday < MaxEntriesPerDay;
        }

        // ---- 틱 진입 (전략 1, 2): 봉 마감을 기다리지 않고 가격이 밴드 안으로 돌아오는 순간 시장가 진입 ----
        //  기준 밴드 = 직전 마감 1분봉의 VWAP/밴드 (차트에 그려진 마지막 값)
        //  전략 2 (대기 중: 직전 종가가 밴드 밖) : 가격이 밴드 가격에 닿는 순간 진입
        //  전략 1 (대기 아님)                  : 이번 1분봉에서 밴드 밖을 찍었다가 밴드 안으로 돌아오는 첫 틱에 진입
        //  방향별로 1분봉당 한 번만 판단 (필터에 막히면 그 봉에서는 다시 보지 않음)
        private void OnIntrabarTick()
        {
            double price = Closes[TickBip][0];
            if (price > tickBarHigh) tickBarHigh = price;
            if (price < tickBarLow) tickBarLow = price;
            OnOpeningDriveTick(price);
            if (!ctxReady)
                return;

            // 전략 2 대기 중 BB 돌파 기록 (완익 후 스위칭 필터용)
            if (armedLong && price < ctxBbLower) bbLowSinceArmed = true;
            if (armedShort && price > ctxBbUpper) bbHighSinceArmed = true;

            TryOverheatEntry(price);

            // 완익 후 반대 진입 조건: 쿨다운이 끝난 뒤 BB에 새로 닿았는지 기록 (마감 불필요, 틱 터치면 됨. 쿨다운 중 터치는 인정 안 함)
            if (fullProfitSide != MarketPosition.Flat && !InFullProfitCooldown(NewYorkTime(Times[TickBip][0])))
            {
                if (price >= ctxBbUpper) bbHighAfterCooldown = true;
                if (price <= ctxBbLower) bbLowAfterCooldown = true;
            }

            bool armed = armedLong || armedShort;
            bool longCandidate, shortCandidate;
            if (armed)
            {
                longCandidate = armedLong && price >= ctxLower;
                shortCandidate = armedShort && price <= ctxUpper;
            }
            else
            {
                bool pierceLow = tickBarLow < ctxLower;
                bool pierceHigh = tickBarHigh > ctxUpper;
                if (pierceLow && pierceHigh)   // 한 봉이 양쪽 다 이탈하면 방향 불명확 -> 스킵
                    return;
                longCandidate = pierceLow && price >= ctxLower;
                shortCandidate = pierceHigh && price <= ctxUpper;
            }
            longCandidate = longCandidate && !triedLongThisBar;
            shortCandidate = shortCandidate && !triedShortThisBar;
            if (!longCandidate && !shortCandidate)
                return;

            DateTime ny = NewYorkTime(Times[TickBip][0]);
            if (!IsVwapTradeTime(ToTime(ny)) || lastSignalBar > CurrentBars[0] || !VwapEntryGatesOpen())
                return;

            // 완익 후 반대 방향이면 쿨다운 이후 BB 터치가 있어야 함. 아직 아니면 이번 틱은 넘기고 다음 틱에서 다시 확인
            if (longCandidate && !BbSwitchOk(true, price, ctxBbUpper, ctxBbLower, ny))
                longCandidate = false;
            if (shortCandidate && !BbSwitchOk(false, price, ctxBbUpper, ctxBbLower, ny))
                shortCandidate = false;
            if (!longCandidate && !shortCandidate)
                return;

            bool isLong = longCandidate;
            if (isLong) triedLongThisBar = true; else triedShortThisBar = true;
            if (armed)
                TryBandReentry(isLong, price, ny, "TICK@band");
            else
                TryBandTouch(isLong, price, ny);
        }

        // 전략 3: 9:30 ET 시가 통과 (틱 진입)
        private void OnOpeningDriveTick(double price)
        {
            if (!orbActiveBar)
                return;
            DateTime ny = NewYorkTime(Times[TickBip][0]);
            int t = ToTime(ny);
            if (t < OpenTime || t >= OrbEntryEnd)
                return;

            // 9:30 시가만 기록 (09:30:00 이후 첫 틱)
            if (!orbRefSet)
            {
                orbRef = price;
                orbRefSet = true;
                orbLastSide = 0;
                if (DebugOutput)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET OPEN DRIVE ref(9:30 open)={1}", ny, orbRef));
                return;
            }
            // 9:30:00~9:30:59: 시가만 기록하고 아무것도 하지 않음
            if (t < OrbEntryStart)
                return;

            int side = price > orbRef ? 1 : price < orbRef ? -1 : 0;
            if (side == 0 || side == orbLastSide)
                return;
            bool startSide = orbLastSide == 0;   // 9:31 이후 첫 위치 = 출발 위치 (통과가 아님)
            orbLastSide = side;
            if (startSide && !OpenEnterOnFirstMove)
            {
                if (DebugOutput)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET OPEN DRIVE start side={1} (price={2} vs open={3}), waiting for a cross",
                        ny, side > 0 ? "ABOVE" : "BELOW", price, orbRef));
                return;
            }
            if (orbTradesToday >= OpenMaxTrades)
                return;

            bool isLong = side > 0;
            if (!CanEnter(isLong, ny, "OPEN DRIVE"))
                return;

            string name = isLong ? "OrbLong" : "OrbShort";
            double stopTicks = PointsToTicks(OpenStopPoints);
            double targetTicks = PointsToTicks(OpenTargetPoints);
            int qty = Math.Max(1, OpenQuantity);
            orbTradesToday++;
            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET OPEN DRIVE {1} price={2} crossed ref={3} TP=fill{4}{5}pt SL=fill{6}{7}pt qty={8} (exit 9:40 ET)",
                    ny, isLong ? "LONG" : "SHORT", price, orbRef, isLong ? "+" : "-", OpenTargetPoints, isLong ? "-" : "+", OpenStopPoints, qty));
            SubmitOrDefer(isLong, ny, "OPEN DRIVE", () =>
            {
                // Ticks 모드: 실제 체결 평단 기준 ± pt
                SetProfitTarget(name, CalculationMode.Ticks, targetTicks);
                SetStopLoss(name, CalculationMode.Ticks, stopTicks, false);
                if (isLong)
                    EnterLong(0, qty, name);
                else
                    EnterShort(0, qty, name);
            });
        }

        // V4 BB 추세 필터: BB 하단 > VWAP이면 숏 금지, BB 상단 < VWAP이면 롱 금지 (직전 마감 봉 기준)
        private bool BbTrendBlocks(bool isLong, DateTime ny)
        {
            if (!UseBbTrendFilter)
                return false;
            bool blocked = isLong ? ctxBbUpper < ctxVwap : ctxBbLower > ctxVwap;
            if (blocked && DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET BLOCKED {1} by BB-TREND: bb{2}={3:F2} {4} vwap={5:F2}",
                    ny, isLong ? "LONG" : "SHORT", isLong ? "Upper" : "Lower", isLong ? ctxBbUpper : ctxBbLower,
                    isLong ? "<" : ">", ctxVwap));
            return blocked;
        }

        // V4 과열 진입: BB 하단 > VWAP 상단 밴드면 가격이 상단 밴드에 닿는 틱에 시장가 숏,
        //  BB 상단 < VWAP 하단 밴드면 가격이 하단 밴드에 닿는 틱에 시장가 롱. 익절 = VWAP, 손절 = 전략 1 설정
        private void TryOverheatEntry(double price)
        {
            if (!UseBbOverheatEntry)
                return;
            bool heatShort = UseShorts && !heatShortUsed && ctxBbLower > ctxUpper && price >= ctxUpper;
            bool heatLong = UseLongs && !heatLongUsed && ctxBbUpper < ctxLower && price <= ctxLower;
            if (!heatShort && !heatLong)
                return;

            DateTime ny = NewYorkTime(Times[TickBip][0]);
            if (!IsVwapTradeTime(ToTime(ny)) || lastSignalBar > CurrentBars[0] || !VwapEntryGatesOpen())
                return;
            bool isLong = heatLong;
            if (!CanEnter(isLong, ny, "OVERHEAT"))
                return;

            double tpA = Instrument.MasterInstrument.RoundToTickSize(ctxVwap);
            if (isLong ? tpA <= price + TickSize : tpA >= price - TickSize)
                return;
            double stopPts = StopDistance(isLong, TouchStopPoints, TouchStopAtrMultiple, TouchStopMaxPoints);
            double stopTicks = PointsToTicks(stopPts);
            string nameA = isLong ? "HeatLong" : "HeatShort";
            int qtyA = Math.Max(1, LegAQuantity);
            if (isLong) heatLongUsed = true; else heatShortUsed = true;
            lastSignalBar = CurrentBars[0] + 1;
            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET OVERHEAT {1} price={2} band={3:F2} bb{4}={5:F2} TP(VWAP)={6} SL=fill{7}{8}pt qty={9}",
                    ny, isLong ? "LONG" : "SHORT", price, isLong ? ctxLower : ctxUpper, isLong ? "Upper" : "Lower",
                    isLong ? ctxBbUpper : ctxBbLower, tpA, isLong ? "-" : "+", stopPts, qtyA));

            SubmitOrDefer(isLong, ny, "OVERHEAT", () =>
            {
                SetProfitTarget(nameA, CalculationMode.Price, tpA);
                SetStopLoss(nameA, CalculationMode.Ticks, stopTicks, false);
                ResetLegBTracking();
                activeLegAQuantity = qtyA;
                activeLegAName = nameA;
                activeTpA = tpA;
                if (isLong)
                    EnterLong(0, qtyA, nameA);
                else
                    EnterShort(0, qtyA, nameA);
            });
        }

        // 전략 1: 밴드 터치 되돌림 (틱 진입). 필터는 직전 마감 봉 기준 값 사용, 손절은 진입 평단 ± ATR 기반 손절 폭 (StopDistance)
        private void TryBandTouch(bool isLong, double price, DateTime ny)
        {
            if (isLong ? !UseLongs : !UseShorts)
                return;
            if (BbTrendBlocks(isLong, ny))
                return;
            string side = isLong ? "LONG" : "SHORT";
            DateTime tickTime = Times[TickBip][0];

            if (UseTrendFilter)
            {
                if (!trendReady || trendTimestamp > tickTime || (tickTime - trendTimestamp).TotalMinutes > 10)
                    return;
                if (trendDirection != (isLong ? 1 : -1))
                {
                    if (DebugOutput)
                        Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET BLOCKED {1} by 5M-EMA: trendDirection={2} ema={3:F2}",
                            ny, side, trendDirection, trendEmaValue));
                    return;
                }
            }

            // V6d 필터: 현재가 vs 5분 EMA (EMA 위 -> 롱만, EMA 아래 -> 숏만)
            if (UsePriceSideFilter)
            {
                if (!trendReady)
                    return;
                if (isLong ? price <= trendEmaValue : price >= trendEmaValue)
                {
                    if (isLong) sideBlockedLongs++; else sideBlockedShorts++;
                    if (DebugOutput)
                        Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET BLOCKED {1} by EMA-SIDE: price={2} ema5m={3:F2}",
                            ny, side, price, trendEmaValue));
                    return;
                }
            }

            // V6c 필터 1: 밴드 워크 (종가가 밴드 밖에서 연속 N봉 마감 중이면 역방향 금지)
            int walkCount = isLong ? barsBelowLower : barsAboveUpper;
            if (UseBandWalkFilter && walkCount >= WalkBars)
            {
                if (isLong) walkBlockedLongs++; else walkBlockedShorts++;
                if (DebugOutput)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET BLOCKED {1} by BAND-WALK: closes outside={2} (limit {3})",
                        ny, side, walkCount, WalkBars));
                return;
            }

            // V6c 필터 2: VWAP 기울기 (VWAP이 한 방향으로 가파르게 움직이면 역방향 금지)
            if (UseVwapSlopeFilter && ctxSlopeReady && (isLong ? ctxSlopeTicks < -MaxSlopeTicksPerBar : ctxSlopeTicks > MaxSlopeTicksPerBar))
            {
                if (isLong) slopeBlockedLongs++; else slopeBlockedShorts++;
                if (DebugOutput)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET BLOCKED {1} by VWAP-SLOPE: slope={2:F2} ticks/bar (limit {3:F2}) vwap={4:F2}",
                        ny, side, ctxSlopeTicks, MaxSlopeTicksPerBar, ctxVwap));
                return;
            }

            if (isLong) longSignals++; else shortSignals++;

            if (CurrentBars[0] - (isLong ? lastLongStopBar : lastShortStopBar) <= CooldownBars)
                return;
            if (!CanEnter(isLong, ny, "SIGNAL"))
                return;

            double tpA = Instrument.MasterInstrument.RoundToTickSize(ctxVwap);
            double tpB = Instrument.MasterInstrument.RoundToTickSize(isLong ? ctxUpper : ctxLower);   // 반대편 밴드 끝
            // 목표가 진입가보다 유리한 쪽에 있어야 함
            if (isLong ? (tpA <= price + TickSize || tpB <= tpA)
                       : (tpA >= price - TickSize || tpB >= tpA))
                return;
            double stopPts = StopDistance(isLong, TouchStopPoints, TouchStopAtrMultiple, TouchStopMaxPoints);
            if (!RewardRiskOk(isLong, price, tpA, stopPts, ny, "SIGNAL"))
                return;
            double stopTicks = PointsToTicks(stopPts);   // 손절 = 실제 체결 평단 ± stopPts

            string nameA = isLong ? "LongA" : "ShortA";
            string nameB = isLong ? "LongB" : "ShortB";
            lastSignalBar = CurrentBars[0] + 1;   // 지금 만들어지는 1분봉에서는 더 진입하지 않음
            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET {1} SIGNAL (tick back inside band) price={2} TPA(VWAP)={3} TPB(band)={4} SL=fill{5}{6}pt",
                    ny, side, price, tpA, tpB, isLong ? "-" : "+", stopPts));

            int qtyA, qtyB;
            GetEntryQuantities(out qtyA, out qtyB);
            SubmitOrDefer(isLong, ny, "SIGNAL", () =>
            {
                // 1) 타겟 먼저, 2) 그다음 스탑 (본절 이동으로 바뀐 B 스탑도 여기서 원래 값으로 초기화됨)
                SetProfitTarget(nameA, CalculationMode.Price, tpA);
                if (UseLegB)
                    SetProfitTarget(nameB, CalculationMode.Price, tpB);
                SetStopLoss(nameA, CalculationMode.Ticks, stopTicks, false);
                if (UseLegB)
                    SetStopLoss(nameB, CalculationMode.Ticks, stopTicks, false);

                ResetLegBTracking();
                activeLegAQuantity = qtyA;
                activeLegBName = nameB;
                activeLegAName = nameA;
                activeTpA = tpA;
                activeTpB = tpB;
                // 주문은 1분봉 시리즈(0)로 냄 (청산 주문과 같은 시리즈)
                if (isLong)
                {
                    EnterLong(0, qtyA, nameA);
                    if (UseLegB)
                        EnterLong(0, qtyB, nameB);
                }
                else
                {
                    EnterShort(0, qtyA, nameA);
                    if (UseLegB)
                        EnterShort(0, qtyB, nameB);
                }
            });
        }

        // 전략 2: 종가가 밴드 밖에서 마감한 뒤 가격이 밴드로 돌아올 때 시장가 진입
        //  틱 진입(가격이 밴드에 닿는 순간) 또는 봉 마감 대체 진입(밴드가 움직여 틱으로 못 잡은 경우)
        //  SL = 체결 평단 ± ATR 기반 손절 폭 (StopDistance) / A 레그 = VWAP / B 레그 = 반대편 밴드 끝
        private void TryBandReentry(bool isLong, double price, DateTime ny, string mode)
        {
            if (isLong ? !UseLongs : !UseShorts)
                return;
            if (BbTrendBlocks(isLong, ny))
                return;
            // 전략 2 VWAP 기울기 필터 (전략 1과 별도 토글/임계값). 막혀도 armed 상태는 유지 -> 기울기가 누그러지면 다음 봉에서 다시 판단
            if (UseReentrySlopeFilter && ctxSlopeReady
                && (isLong ? ctxSlopeTicks < -ReentryMaxSlopeTicksPerBar : ctxSlopeTicks > ReentryMaxSlopeTicksPerBar))
            {
                if (isLong) reentrySlopeBlockedLongs++; else reentrySlopeBlockedShorts++;
                if (DebugOutput)
                    Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET BLOCKED REENTRY {1} {2} by VWAP-SLOPE: slope={3:F2} ticks/bar (limit {4:F2}) vwap={5:F2}",
                        ny, isLong ? "LONG" : "SHORT", mode, ctxSlopeTicks, ReentryMaxSlopeTicksPerBar, ctxVwap));
                return;
            }

            if (CurrentBars[0] - (isLong ? lastLongStopBar : lastShortStopBar) <= CooldownBars)
                return;
            if (!CanEnter(isLong, ny, "REENTRY"))
                return;

            string nameA = isLong ? "RevLongA" : "RevShortA";
            string nameB = isLong ? "RevLongB" : "RevShortB";
            double tpA = Instrument.MasterInstrument.RoundToTickSize(ctxVwap);
            double tpB = Instrument.MasterInstrument.RoundToTickSize(isLong ? ctxUpper : ctxLower);

            // 목표가 진입가보다 유리한 쪽에 있어야 함
            if (isLong ? (tpA <= price + TickSize || tpB <= tpA)
                       : (tpA >= price - TickSize || tpB >= tpA))
                return;

            double stopPts = StopDistance(isLong, ReentryStopPoints, ReentryStopAtrMultiple, ReentryStopMaxPoints);
            if (!RewardRiskOk(isLong, price, tpA, stopPts, ny, "REENTRY"))
                return;
            double stopTicks = PointsToTicks(stopPts);
            int qtyA = Math.Max(1, LegAQuantity);
            int qtyB = Math.Max(1, LegQuantity);
            lastSignalBar = CurrentBars[0] + 1;

            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET REENTRY {1} {2} price={3} TPA(VWAP)={4} TPB(opposite band)={5} SL=fill{6}{7}pt qty={8}+{9}",
                    ny, isLong ? "LONG" : "SHORT", mode, price, tpA, tpB, isLong ? "-" : "+", stopPts, qtyA, qtyB));

            SubmitOrDefer(isLong, ny, "REENTRY", () =>
            {
                SetProfitTarget(nameA, CalculationMode.Price, tpA);
                if (UseLegB)
                    SetProfitTarget(nameB, CalculationMode.Price, tpB);
                // Ticks 모드: 실제 체결 평단 기준 ± stopPts (본절 이동으로 바뀐 B 스탑도 여기서 초기화)
                SetStopLoss(nameA, CalculationMode.Ticks, stopTicks, false);
                if (UseLegB)
                    SetStopLoss(nameB, CalculationMode.Ticks, stopTicks, false);

                ResetLegBTracking();
                activeLegAQuantity = qtyA;
                activeLegBName = nameB;
                activeLegAName = nameA;
                activeTpA = tpA;
                activeTpB = tpB;
                if (isLong)
                {
                    EnterLong(0, qtyA, nameA);
                    if (UseLegB)
                        EnterLong(0, qtyB, nameB);
                }
                else
                {
                    EnterShort(0, qtyA, nameA);
                    if (UseLegB)
                        EnterShort(0, qtyB, nameB);
                }
            });
        }

        // 9:30 ET 봉 마감: 남은 VWAP 전략 포지션 정리 (FlatVwapAtOpen). 9:30~9:45는 신규 진입 없음
        private void FlatVwapPositionAtOpen(DateTime ny, int hhmmss)
        {
            if (openFlatDone || hhmmss < OpenTime)
                return;
            openFlatDone = true;
            if (!FlatVwapAtOpen || Position.MarketPosition == MarketPosition.Flat)
                return;
            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm} ET OPEN FLAT: closing VWAP position {1} x{2}",
                    ny, Position.MarketPosition, Position.Quantity));
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong();
            else
                ExitShort();
        }

        // A 레그 전체 목표 체결과 B 레그 진입 체결이 모두 확인된 경우에만 스탑을 이동한다.
        // 두 이벤트의 순서가 바뀌어도 양쪽 이벤트에서 이 함수를 호출하므로 이동 누락을 줄인다.
        private void TryMoveLegBStopAfterATarget(DateTime time)
        {
            if (!MoveStopToBreakevenAtVwap || breakevenMoved || activeLegAQuantity <= 0)
                return;
            if (legATargetFilledQty < activeLegAQuantity || legBFillQty <= 0)
                return;
            double averageTargetFill = legATargetFillValue / legATargetFilledQty;
            MoveLegBStopToBreakeven(time, averageTargetFill);
        }

        // A 레그가 VWAP 타겟에 체결된 뒤: B 레그 손절을 본절(B 진입 평단가)로 이동
        //  targetFillPrice: A 타겟 체결가. 히스토리(백테스트)에서는 GetCurrentBid/Ask가 직전 봉 종가를
        //  돌려줘서 "이미 평단가 아래"로 잘못 판단하므로, 실시간이 아닐 때는 체결가를 현재가로 씀
        private void MoveLegBStopToBreakeven(DateTime time, double targetFillPrice)
        {
            if (breakevenMoved || legBFillQty <= 0)
                return;
            breakevenMoved = true;

            bool realtime = State == State.Realtime;
            double level = Instrument.MasterInstrument.RoundToTickSize(legBFillValue / legBFillQty);

            if (Position.MarketPosition == MarketPosition.Long)
            {
                // 이미 본절 아래면 스탑을 걸 수 없으므로 시장가 청산
                double bid = realtime ? GetCurrentBid() : targetFillPrice;
                if (bid <= level)
                    ExitLong("BEExitLB", activeLegBName);
                else
                    SetStopLoss(activeLegBName, CalculationMode.Price, level, false);
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                double ask = realtime ? GetCurrentAsk() : targetFillPrice;
                if (ask >= level)
                    ExitShort("BEExitSB", activeLegBName);
                else
                    SetStopLoss(activeLegBName, CalculationMode.Price, level, false);
            }
            else
                return;

            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm} ET LEG-B STOP MOVED -> breakeven {1} leg={2} (leg A filled at {3})",
                    NewYorkTime(time), level, activeLegBName, targetFillPrice));
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;
            lastExecutionTime = time;   // 완익 쿨다운 시작 시각용
            string name = execution.Order.Name;
            string from = execution.Order.FromEntrySignal;

            if (DebugOutput)
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ET EXEC name='{1}' from='{2}' action={3} state={4} qty={5} price={6:F2} orderFilled={7}/{8} position={9}",
                    NewYorkTime(time), name, from, execution.Order.OrderAction, execution.Order.OrderState,
                    quantity, price, execution.Order.Filled, execution.Order.Quantity, marketPosition));

            // A 레그 진입 체결을 기준으로 VWAP 전략(1, 2) 거래 횟수 카운트
            if (IsLegAEntry(name) && countedEntryOrders.Add(orderId))
            {
                entriesToday++;
                if (name.StartsWith("Rev"))
                    reentryEntries++;
            }

            // 완익 판정용: VWAP 매매(전략 1, 2)가 새로 시작되면 이전 완익 제한을 풀고 이번 매매 추적 시작.
            // 청산 체결 중 하나라도 익절 주문(Profit target)이 아니면 완익이 아님 (손절, 본절, 시간 청산 등)
            if (IsLegAEntry(name) || IsLegBEntry(name))
            {
                if (!vwapTradeOpen)
                {
                    vwapTradeOpen = true;
                    vwapTradeAllTargets = true;
                    vwapTradeSide = marketPosition;
                    fullProfitSide = MarketPosition.Flat;
                }
            }
            else if (vwapTradeOpen && name != "Profit target")
                vwapTradeAllTargets = false;

            // 전략 2 진입이 체결되면 대기 상태 해제 (다시 밴드 밖에서 마감해야 새로 대기)
            if (name == "RevLongA" || name == "RevLongB")
                armedLong = false;
            else if (name == "RevShortA" || name == "RevShortB")
                armedShort = false;

            // B 레그 평단가 계산 (부분 체결 포함)
            if (IsLegBEntry(name))
            {
                legBFillValue += price * quantity;
                legBFillQty += quantity;
                if (DebugOutput)
                    Print(string.Format("B FILL TRACK: qty={0}, avg={1:F2}", legBFillQty,
                        legBFillQty > 0 ? legBFillValue / legBFillQty : 0));

                // B 진입 체결이 A 목표 체결보다 늦게 도착하는 경우도 처리한다.
                TryMoveLegBStopAfterATarget(time);
            }

            // VWAP 전략 손절 체결 시 쿨다운 기준 봉 기록 (시가 통과 전략 손절은 제외)
            if (name == "Stop loss" && !IsOpeningDriveEntry(from))
            {
                if (execution.Order.OrderAction == OrderAction.Sell)
                    lastLongStopBar = CurrentBars[0];
                else if (execution.Order.OrderAction == OrderAction.BuyToCover)
                    lastShortStopBar = CurrentBars[0];
            }

            // A 레그의 VWAP 목표가 부분 체결을 누적한다.
            // A 전체 수량이 실제로 목표 체결된 뒤에만 B 레그 스탑을 이동한다.
            bool isLegATarget = name == "Profit target" && IsLegAEntry(from);
            if (isLegATarget)
            {
                legATargetFilledQty += quantity;
                legATargetFillValue += price * quantity;
                if (DebugOutput)
                    Print(string.Format("A TARGET TRACK: filled={0}/{1}, avgFill={2:F2}",
                        legATargetFilledQty, activeLegAQuantity,
                        legATargetFilledQty > 0 ? legATargetFillValue / legATargetFilledQty : 0));
                TryMoveLegBStopAfterATarget(time);
            }
        }

        // 포지션이 플랫으로 바뀐 시각과 직전 방향 기록 (반대 방향 재진입 지연용)
        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity,
            MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat && lastPositionSide != MarketPosition.Flat)
            {
                lastClosedSide = lastPositionSide;
                lastFlatTime = DateTime.Now;

                // VWAP 매매가 전량 익절 주문으로 끝났으면(완익) 반대 방향 다음 진입에 BB 조건을 건다
                if (vwapTradeOpen)
                {
                    vwapTradeOpen = false;
                    fullProfitSide = vwapTradeAllTargets ? vwapTradeSide : MarketPosition.Flat;
                    if (fullProfitSide != MarketPosition.Flat)
                    {
                        // 쿨다운 시작 + BB 터치 기록 초기화 (쿨다운이 끝난 뒤 새로 닿아야 반대 진입)
                        fullProfitTimeNy = NewYorkTime(lastExecutionTime != DateTime.MinValue ? lastExecutionTime : Time[0]);
                        bbHighAfterCooldown = bbLowAfterCooldown = false;
                        cooldownBlockPrinted = false;
                        if (DebugOutput)
                            Print(string.Format("{0:yyyy-MM-dd HH:mm:ss} ET FULL PROFIT {1} -> no entries until {2:HH:mm:ss} ET{3}",
                                fullProfitTimeNy, fullProfitSide, fullProfitTimeNy.AddMinutes(FullProfitCooldownMinutes),
                                UseBbSwitchFilter ? string.Format(", then next {0} entry needs a Bollinger {1} touch after that",
                                    fullProfitSide == MarketPosition.Long ? "SHORT" : "LONG",
                                    fullProfitSide == MarketPosition.Long ? "upper" : "lower") : ""));
                    }
                }
            }
            lastPositionSide = marketPosition;
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (order == null)
                return;

            // 양방향 방지용: 살아 있는 진입 주문 목록 유지 (체결/취소/거부되면 제거)
            if (IsEntryName(order.Name))
            {
                bool terminal = orderState == OrderState.Filled || orderState == OrderState.Cancelled
                    || orderState == OrderState.Rejected;
                if (terminal)
                    workingEntryOrders.Remove(order);
                else if (!workingEntryOrders.Contains(order))
                    workingEntryOrders.Add(order);
            }

            // 진단 모드에서는 타겟/스탑의 가격, 상태, 체결 수량을 모두 출력해
            // 차트상 터치와 실제 주문 체결이 달랐는지 확인할 수 있게 한다.
            if (DebugOutput &&
                (order.Name == "Profit target" || order.Name == "Stop loss" || error != ErrorCode.NoError))
            {
                Print(string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} ET ORDER name='{1}' from='{2}' state={3} action={4} qty={5} filled={6} avgFill={7:F2} limit={8:F2} stop={9:F2} error={10} comment='{11}'",
                    NewYorkTime(time), order.Name, order.FromEntrySignal, orderState,
                    order.OrderAction, quantity, filled, averageFillPrice, limitPrice,
                    stopPrice, error, comment));
            }

            if (DebugOutput && error != ErrorCode.NoError)
                Print(string.Format("ORDER ERROR {0}: {1} {2}", order.Name, error, comment));
        }

        #region Rendering (5분 EMA)
        public override void OnRenderTargetChanged()
        {
            DisposeDxBrushes();
            if (RenderTarget == null)
                return;
            emaDx = EmaBrush.ToDxBrush(RenderTarget);
        }

        private void DisposeDxBrushes()
        {
            if (emaDx != null) { emaDx.Dispose(); emaDx = null; }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            // VWAP/밴드 플롯과 체결 마커 등 기본 렌더링 유지
            base.OnRender(chartControl, chartScale);

            // 실시간에는 OnBarUpdate가 리스트에 추가하는 중일 수 있으므로 두 리스트 중 짧은 쪽 기준
            int count = Math.Min(emaTimes.Count, emaValues.Count);
            if (!ShowEma || ChartBars == null || RenderTarget == null || emaDx == null || count < 2)
                return;

            // 화면에 보이는 시간 범위
            DateTime fromTime = ChartBars.GetTimeByBarIdx(chartControl, ChartBars.FromIndex);
            DateTime toTime = ChartBars.GetTimeByBarIdx(chartControl, ChartBars.ToIndex);

            // 5분봉 EMA 점(5분봉 종료 시각, EMA 값)을 직선으로 이어서 그림
            for (int i = 1; i < count; i++)
            {
                if (emaTimes[i] < fromTime)
                    continue;
                if (emaTimes[i - 1] > toTime)
                    break;

                SharpDX.Vector2 p0 = new SharpDX.Vector2(
                    chartControl.GetXByTime(emaTimes[i - 1]), chartScale.GetYByValue(emaValues[i - 1]));
                SharpDX.Vector2 p1 = new SharpDX.Vector2(
                    chartControl.GetXByTime(emaTimes[i]), chartScale.GetYByValue(emaValues[i]));
                RenderTarget.DrawLine(p0, p1, emaDx, EmaWidth);
            }
        }
        #endregion

        #region Parameters
        [NinjaScriptProperty]
        [Display(Name = "Use leg B (opposite band target)", Order = 1, GroupName = "V4")]
        public bool UseLegB { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Longs (strategies 1, 2)", Order = 2, GroupName = "V4")]
        public bool UseLongs { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Shorts (strategies 1, 2)", Order = 3, GroupName = "V4")]
        public bool UseShorts { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "BB trend filter (BB lower > VWAP: no shorts / BB upper < VWAP: no longs)", Order = 4, GroupName = "V4")]
        public bool UseBbTrendFilter { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Overheat entry (BB lower > VWAP upper band: short at band touch / long mirrored)", Order = 5, GroupName = "V4")]
        public bool UseBbOverheatEntry { get; set; }
        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Leg A quantity (VWAP target)", Order = 0, GroupName = "Risk")]
        public int LegAQuantity { get; set; }
        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Leg B quantity (opposite band target)", Order = 1, GroupName = "Risk")]
        public int LegQuantity { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Leg B stop to breakeven when leg A hits VWAP", Order = 10, GroupName = "Risk")]
        public bool MoveStopToBreakevenAtVwap { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Use ATR-based stops for LONGS (strategies 1, 2)", Order = 2, GroupName = "Risk")]
        public bool UseAtrStops { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Use ATR-based stops for SHORTS (strategies 1, 2)", Order = 2, GroupName = "Risk")]
        public bool UseAtrStopsShort { get; set; }
        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "ATR period (1-min)", Order = 3, GroupName = "Risk")]
        public int AtrPeriod { get; set; }
        [NinjaScriptProperty, Range(0.25, 200.0)]
        [Display(Name = "Strategy 1 stop: minimum / fixed if ATR off (points)", Order = 4, GroupName = "Risk")]
        public double TouchStopPoints { get; set; }
        [NinjaScriptProperty, Range(0.1, 20.0)]
        [Display(Name = "Strategy 1 stop: ATR multiple", Order = 5, GroupName = "Risk")]
        public double TouchStopAtrMultiple { get; set; }
        [NinjaScriptProperty, Range(0.25, 500.0)]
        [Display(Name = "Strategy 1 stop: maximum (points)", Order = 6, GroupName = "Risk")]
        public double TouchStopMaxPoints { get; set; }
        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Same-direction cooldown bars", Order = 6, GroupName = "Risk")]
        public int CooldownBars { get; set; }
        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Max VWAP entries per NY day (0=unlimited)", Order = 7, GroupName = "Risk")]
        public int MaxEntriesPerDay { get; set; }
        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "VWAP standard deviations (until 9:30 ET)", Order = 1, GroupName = "Signals")]
        public double BandDeviations { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Use 5-min trend filter", Order = 3, GroupName = "Signals")]
        public bool UseTrendFilter { get; set; }
        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "5-minute EMA period", Order = 4, GroupName = "Signals")]
        public int EmaPeriod { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Price vs 5-min EMA (above = long only, below = short only)", Order = 5, GroupName = "Signals")]
        public bool UsePriceSideFilter { get; set; }

        [NinjaScriptProperty, Range(-5.0, 5.0)]
        [Display(Name = "Lower band offset until 9:30 ET (SD, + wider / - narrower)", Order = 4, GroupName = "VWAP")]
        public double LowerBandOffsetPreOpen { get; set; }
        [NinjaScriptProperty, Range(-5.0, 5.0)]
        [Display(Name = "Lower band offset after 9:30 ET (SD, + wider / - narrower)", Order = 5, GroupName = "VWAP")]
        public double LowerBandOffsetPostOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Post-open VWAP trading hours (session start 9:30 ET)", Order = 2, GroupName = "VWAP")]
        public string PostOpenVwapHours { get; set; }
        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Band standard deviations after 9:30 ET", Order = 3, GroupName = "VWAP")]
        public double PostOpenBandDeviations { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use re-entry after close outside band", Order = 1, GroupName = "Band Re-entry")]
        public bool UseBandReentry { get; set; }
        [NinjaScriptProperty, Range(0.25, 200.0)]
        [Display(Name = "Strategy 2 stop: minimum / fixed if ATR off (points)", Order = 2, GroupName = "Band Re-entry")]
        public double ReentryStopPoints { get; set; }
        [NinjaScriptProperty, Range(0.1, 20.0)]
        [Display(Name = "Strategy 2 stop: ATR multiple", Order = 3, GroupName = "Band Re-entry")]
        public double ReentryStopAtrMultiple { get; set; }
        [NinjaScriptProperty, Range(0.25, 500.0)]
        [Display(Name = "Strategy 2 stop: maximum (points)", Order = 4, GroupName = "Band Re-entry")]
        public double ReentryStopMaxPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Close VWAP positions at 9:30 ET", Order = 2, GroupName = "Order Safety")]
        public bool FlatVwapAtOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use 9:30 ET open-price cross", Order = 1, GroupName = "Opening Drive")]
        public bool UseOpeningDrive { get; set; }
        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Open-drive quantity", Order = 2, GroupName = "Opening Drive")]
        public int OpenQuantity { get; set; }
        [NinjaScriptProperty, Range(0.25, 200.0)]
        [Display(Name = "Open-drive stop (points from entry price)", Order = 3, GroupName = "Opening Drive")]
        public double OpenStopPoints { get; set; }
        [NinjaScriptProperty, Range(0.25, 200.0)]
        [Display(Name = "Open-drive target (points from entry price)", Order = 4, GroupName = "Opening Drive")]
        public double OpenTargetPoints { get; set; }
        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Open-drive max trades per day", Order = 5, GroupName = "Opening Drive")]
        public int OpenMaxTrades { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Also enter at 09:31 in the direction price is from the open", Order = 6, GroupName = "Opening Drive")]
        public bool OpenEnterOnFirstMove { get; set; }

        [NinjaScriptProperty, Range(0.0, 10.0)]
        [Display(Name = "Delay before opposite-direction entry after a close (seconds, realtime only)", Order = 1, GroupName = "Order Safety")]
        public double OppositeEntryDelaySeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "After full profit, opposite entry needs a Bollinger touch (after cooldown)", Order = 1, GroupName = "Switch Filter (Bollinger)")]
        public bool UseBbSwitchFilter { get; set; }
        [NinjaScriptProperty, Range(2, 200)]
        [Display(Name = "Bollinger period", Order = 2, GroupName = "Switch Filter (Bollinger)")]
        public int BbPeriod { get; set; }
        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Bollinger standard deviations", Order = 3, GroupName = "Switch Filter (Bollinger)")]
        public double BbStdDev { get; set; }
        [NinjaScriptProperty, Range(0.0, 60.0)]
        [Display(Name = "After full profit, no entries for (minutes)", Order = 4, GroupName = "Switch Filter (Bollinger)")]
        public double FullProfitCooldownMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use reward/risk filter (distance to VWAP / stop)", Order = 1, GroupName = "Reward/Risk Filter")]
        public bool UseRewardRiskFilter { get; set; }
        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Min reward/risk (1.0 = VWAP at least as far as the stop)", Order = 2, GroupName = "Reward/Risk Filter")]
        public double MinRewardRisk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Move targets each bar to latest VWAP (A) / opposite band (B)", Order = 11, GroupName = "Risk")]
        public bool UpdateTargetsEachBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use band-walk filter", Order = 1, GroupName = "Trend Continuation Filter")]
        public bool UseBandWalkFilter { get; set; }
        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Band-walk closes (N)", Order = 2, GroupName = "Trend Continuation Filter")]
        public int WalkBars { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Use VWAP slope filter (strategy 1)", Order = 3, GroupName = "Trend Continuation Filter")]
        public bool UseVwapSlopeFilter { get; set; }
        [NinjaScriptProperty, Range(2, 100)]
        [Display(Name = "VWAP slope lookback bars (shared)", Order = 4, GroupName = "Trend Continuation Filter")]
        public int SlopeBars { get; set; }
        [NinjaScriptProperty, Range(0.0, 20.0)]
        [Display(Name = "Max VWAP slope (ticks/bar)", Order = 5, GroupName = "Trend Continuation Filter")]
        public double MaxSlopeTicksPerBar { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Use VWAP slope filter (strategy 2)", Order = 6, GroupName = "Trend Continuation Filter")]
        public bool UseReentrySlopeFilter { get; set; }
        [NinjaScriptProperty, Range(0.0, 20.0)]
        [Display(Name = "Max VWAP slope for strategy 2 (ticks/bar)", Order = 7, GroupName = "Trend Continuation Filter")]
        public double ReentryMaxSlopeTicksPerBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP and bands on chart", Order = 1, GroupName = "Display")]
        public bool ShowBands { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show 5-min EMA on chart", Order = 2, GroupName = "Display")]
        public bool ShowEma { get; set; }

        [XmlIgnore]
        [Display(Name = "EMA color", Order = 3, GroupName = "Display")]
        public Brush EmaBrush { get; set; }
        [Browsable(false)]
        public string EmaBrushSerializable
        {
            get { return Serialize.BrushToString(EmaBrush); }
            set { EmaBrush = Serialize.StringToBrush(value); }
        }

        [Range(1, 10)]
        [Display(Name = "EMA line width", Order = 4, GroupName = "Display")]
        public int EmaWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Diagnostic output", Order = 1, GroupName = "Diagnostics")]
        public bool DebugOutput { get; set; }
        #endregion
    }
}
