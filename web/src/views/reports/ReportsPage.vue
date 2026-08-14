<script setup lang="ts">
// 小趴菜 Web 3.0 — 使用报告
// [TASK-PRELAUNCH-P2] 重构：接入真实 API（reportApi.daily/weekly/exportData），移除 Mock 数据；
// 动态分类（后端按终端上报聚合，不写死四类）、日报模板丰富（大数字卡+剩余额度+一句话点评、
// 分类环形图、Top5 应用、时段分布、拦截时间线、与昨日对比）；
// 周报模板丰富（趋势折线、每日明细、环比上周、儿童阅读版）；导出走服务端真实数据（TXT/JSON/CSV）
import { ref, computed, watch, onMounted } from 'vue'
import { useDeviceStore } from '@/stores/devices'
import { useIsMobile } from '@/composables/useIsMobile'
import { reportApi } from '@/api'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { LineChart, BarChart, PieChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, LegendComponent, TitleComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { Download } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import dayjs from 'dayjs'

use([LineChart, BarChart, PieChart, GridComponent, TooltipComponent, LegendComponent, TitleComponent, CanvasRenderer])

// ---- 后端返回结构（与 server/Controllers/ReportsController.cs 对应）----
interface CategoryStat { key: string; name: string; minutes: number; percent: number }
interface AppStat { packageName: string; appName: string; category: string; minutes: number }
interface BlockEvent { time: string; appName: string; category: string }
interface DailyReport {
  date: string
  totalMinutes: number
  limitMinutes: number
  remainingMinutes: number | null
  rawAccumulated: boolean
  categories: CategoryStat[]
  topApps: AppStat[]
  hourlyData: number[]
  blockCount: number
  overtimeCount: number
  events: BlockEvent[]
  previousDayTotalMinutes: number
}
interface WeeklyReport {
  weekStart: string
  weekEnd: string
  totalMinutes: number
  limitMinutes: number
  prevWeekTotalMinutes: number
  dailyTotals: number[]
  dailyDetails: { date: string; totalMinutes: number; blockCount: number }[]
  dates: string[]
  categories: CategoryStat[]
  topApps: AppStat[]
  blockCount: number
  overtimeCount: number
}

const deviceStore = useDeviceStore()
const isMobile = useIsMobile()
const reportType = ref<'daily' | 'weekly'>('daily')
const selectedDeviceId = ref<number | null>(null)
// 日期参数：日报选具体日期，周报选周起始日
const date = ref(dayjs().format('YYYY-MM-DD'))
const weekStart = ref(dayjs().subtract(6, 'day').format('YYYY-MM-DD'))
const daily = ref<DailyReport | null>(null)
const weekly = ref<WeeklyReport | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

const deviceOptions = computed(() => [
  { value: null, label: '全部设备' },
  ...deviceStore.devices.map(d => ({ value: d.id, label: d.name })),
])

// ---- 加载 ----
async function loadReport() {
  loading.value = true
  error.value = null
  try {
    if (reportType.value === 'daily') {
      const res = await reportApi.daily(selectedDeviceId.value ?? undefined, date.value)
      daily.value = res.data
    } else {
      const res = await reportApi.weekly(selectedDeviceId.value ?? undefined, weekStart.value)
      weekly.value = res.data
    }
  } catch (e: any) {
    // [TASK-PRELAUNCH-P2] 移除 Mock 兜底：失败显示错误态，不渲染假数据
    error.value = e.response?.data?.message || e.response?.data?.error || '报告加载失败，请稍后重试'
  } finally {
    loading.value = false
  }
}

watch([reportType, selectedDeviceId, date, weekStart], loadReport)
// [TASK-PRELAUNCH-P2-FIX] 092：初次进入必须加载报告（此前仅 fetchDevices，watch 无 immediate，首屏空白）
onMounted(async () => {
  await deviceStore.fetchDevices()
  await loadReport()
})

// ---- 格式化 ----
function fmt(minutes: number): string {
  return minutes >= 60 ? `${Math.floor(minutes / 60)}h${minutes % 60}min` : `${minutes}min`
}

// ---- 日报：一句话点评 ----
const dailyComment = computed(() => {
  const d = daily.value
  if (!d) return ''
  if (d.totalMinutes === 0) return '今天暂无使用记录，可能是设备离线或尚未上报。'
  let base: string
  const learning = d.categories.find(c => c.key === 'learning')
  if (d.limitMinutes > 0 && d.totalMinutes > d.limitMinutes) {
    base = `今天用了 ${fmt(d.totalMinutes)}，超出每日限额 ${fmt(d.totalMinutes - d.limitMinutes)}，注意控制使用时间。`
  } else if (learning && learning.percent >= 40) {
    base = `学习时长占比 ${learning.percent}%，坚持得很不错！`
  } else if (d.categories.some(c => (c.key === 'game' || c.key === 'video') && c.percent >= 50)) {
    base = '游戏和视频占比偏高，注意劳逸结合哦。'
  } else {
    base = '使用节奏平稳，继续保持。'
  }
  // 与昨日对比
  if (d.previousDayTotalMinutes > 0) {
    const delta = d.totalMinutes - d.previousDayTotalMinutes
    base += delta >= 0 ? `比昨天多 ${fmt(delta)}。` : `比昨天少 ${fmt(-delta)}。`
  }
  return base
})

// ---- 周报：环比上周 ----
const weekOverWeek = computed(() => {
  const w = weekly.value
  if (!w || w.prevWeekTotalMinutes <= 0) return null
  const delta = w.totalMinutes - w.prevWeekTotalMinutes
  return {
    delta,
    pct: Math.round((Math.abs(delta) / w.prevWeekTotalMinutes) * 100),
  }
})

// ---- 周报：儿童阅读版（给孩子的语气）----
const weeklyChildText = computed(() => {
  const w = weekly.value
  if (!w) return ''
  if (w.totalMinutes === 0) return '宝贝，这一周还没有使用记录哦，是不是设备没有开机呀？'
  const learning = w.categories.find(c => c.key === 'learning')
  const h = Math.floor(w.totalMinutes / 60)
  const m = w.totalMinutes % 60
  const studyPart = learning && learning.percent > 0
    ? `其中学习类占了 ${learning.percent}%`
    : '还没有学习类应用的使用记录'
  const praise = learning && learning.percent >= 40
    ? '你把最多的时间花在了学习上，真棒！继续加油！'
    : learning && learning.percent >= 20
      ? '学习和放松要平衡哦，学习的时间还可以再多一点点。'
      : '记得多留一点时间给学习和休息哦，眼睛也要歇一歇。'
  return `亲爱的宝贝：这一周你一共使用了 ${h} 小时 ${m} 分钟，${studyPart}。${praise}`
})

// ---- 图表配置 ----
const dailyPieOption = computed(() => ({
  tooltip: { trigger: 'item' as const, formatter: '{b}: {c}min ({d}%)' },
  legend: { bottom: 0 },
  series: [{
    name: '分类占比', type: 'pie' as const, radius: ['42%', '68%'],
    data: (daily.value?.categories ?? []).map(c => ({ name: c.name, value: c.minutes })),
    label: { formatter: '{b}: {d}%' },
  }],
}))

const hourlyBarOption = computed(() => ({
  tooltip: { trigger: 'axis' as const },
  xAxis: { type: 'category' as const, data: Array.from({ length: 24 }, (_, i) => `${i}:00`), axisLabel: { rotate: 45, fontSize: 10 } },
  yAxis: { type: 'value' as const, name: '分钟' },
  series: [{ name: '使用时长', type: 'bar' as const, data: daily.value?.hourlyData ?? [], itemStyle: { color: '#409EFF' } }],
  grid: { left: 50, right: 20, top: 20, bottom: 50 },
}))

// Top5 应用横向条形图（倒序让时长最大的显示在最上方）
const dailyTopAppsOption = computed(() => {
  const apps = (daily.value?.topApps ?? []).slice(0, 5).slice().reverse()
  return {
    tooltip: { trigger: 'axis' as const, formatter: '{b}: {c} 分钟' },
    grid: { left: 10, right: 40, top: 10, bottom: 10, containLabel: true },
    xAxis: { type: 'value' as const, name: '分钟' },
    yAxis: { type: 'category' as const, data: apps.map(a => a.appName || a.packageName) },
    series: [{ name: '使用时长', type: 'bar' as const, data: apps.map(a => a.minutes), itemStyle: { color: '#67C23A' } }],
  }
})

const weeklyLineOption = computed(() => ({
  tooltip: { trigger: 'axis' as const },
  xAxis: { type: 'category' as const, data: weekly.value?.dates ?? [] },
  yAxis: { type: 'value' as const, name: '分钟' },
  series: [{ name: '每日使用', type: 'line' as const, data: weekly.value?.dailyTotals ?? [], smooth: true, areaStyle: { opacity: .15 } }],
  grid: { left: 50, right: 20, top: 20, bottom: 40 },
}))

// ---- 周报每日明细：状态标签（对照每日限额）----
function dayRefLimit(): number {
  const limit = weekly.value?.limitMinutes ?? 0
  return limit > 0 ? limit : 150 // 未设置限额时的参考阈值
}
function dayTagType(minutes: number) {
  return minutes > dayRefLimit() ? 'danger' : minutes >= Math.round(dayRefLimit() * 0.5) ? 'success' : 'info'
}
function dayTagText(minutes: number) {
  return minutes > dayRefLimit() ? '超标' : minutes >= Math.round(dayRefLimit() * 0.5) ? '正常' : '偏低'
}
function dayPercent(minutes: number) {
  return Math.min(100, Math.round((minutes / dayRefLimit()) * 100))
}

// ---- 导出：走服务端真实数据（blob 下载）----
async function exportReport(format: 'txt' | 'json' | 'csv') {
  const isDaily = reportType.value === 'daily'
  const from = isDaily ? date.value : weekStart.value
  const to = isDaily ? date.value : dayjs(weekStart.value).add(6, 'day').format('YYYY-MM-DD')
  try {
    const res = await reportApi.exportData(format, {
      deviceId: selectedDeviceId.value ?? undefined,
      from, to,
    })
    // 优先取响应头里的文件名，失败则本地构造
    let filename = `xiaopacai-report-${from.replace(/-/g, '')}-${to.replace(/-/g, '')}.${format}`
    const cd = res.headers?.['content-disposition']
    if (cd) {
      const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd)
      if (m) filename = decodeURIComponent(m[1])
    }
    const url = URL.createObjectURL(res.data)
    const a = document.createElement('a')
    a.href = url
    a.download = filename
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success('报告已导出')
  } catch {
    ElMessage.error('导出失败，请稍后重试')
  }
}
</script>

<template>
  <div class="reports-page" v-loading="loading">
    <div class="page-header">
      <h2 class="page-title">使用报告</h2>
      <div class="page-actions">
        <el-select v-model="selectedDeviceId" placeholder="设备" clearable style="width:140px">
          <el-option v-for="d in deviceOptions" :key="String(d.value)" :label="d.label" :value="d.value" />
        </el-select>
        <el-date-picker v-if="reportType==='daily'" v-model="date" type="date" value-format="YYYY-MM-DD"
          :clearable="false" size="small" style="width:150px" />
        <el-date-picker v-else v-model="weekStart" type="date" value-format="YYYY-MM-DD" :clearable="false"
          size="small" style="width:150px" placeholder="周起始日" />
        <el-radio-group v-model="reportType" size="small">
          <el-radio-button value="daily">日报</el-radio-button>
          <el-radio-button value="weekly">周报</el-radio-button>
        </el-radio-group>
        <el-dropdown @command="exportReport">
          <el-button size="small" :icon="Download">导出</el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="txt">TXT</el-dropdown-item>
              <el-dropdown-item command="json">JSON</el-dropdown-item>
              <el-dropdown-item command="csv">CSV</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </div>

    <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" style="margin-bottom:16px">
      <template #default>
        <el-button size="small" @click="loadReport">重试</el-button>
      </template>
    </el-alert>

    <!-- ==================== 日报 ==================== -->
    <template v-if="reportType==='daily' && daily">
      <!-- 大数字卡：总时长 + 剩余额度 + 一句话点评 + 分类 chips -->
      <el-card shadow="hover" class="report-summary">
        <div class="summary-stat">
          <span class="summary-label">{{ daily.date }} · 总使用时长</span>
          <span class="summary-value">{{ daily.totalMinutes }} <small>分钟</small></span>
        </div>
        <div class="summary-sub">
          <span v-if="daily.limitMinutes > 0" class="quota-line">
            每日限额 {{ daily.limitMinutes }} 分钟 ·
            <b :class="{ 'quota-exhausted': daily.remainingMinutes === 0 }">
              {{ daily.remainingMinutes === 0 ? '今日额度已用完' : `剩余额度 ${daily.remainingMinutes ?? 0} 分钟` }}
            </b>
          </span>
          <!-- [TASK-PRELAUNCH-P2] 口径提示：报告为原始累计（含重置前用量），P4 再统一调整后口径 -->
          <el-tag v-if="daily.rawAccumulated" size="small" type="info" effect="plain">原始累计口径（含重置前用量）</el-tag>
        </div>
        <p class="daily-comment">💬 {{ dailyComment }}</p>
        <div v-if="daily.categories.length" class="summary-detail">
          <span v-for="c in daily.categories" :key="c.key" class="cat-chip">{{ c.name }} {{ c.minutes }}min（{{ c.percent }}%）</span>
        </div>
      </el-card>

      <el-alert v-if="daily.totalMinutes === 0" title="当天暂无使用记录，图表为空；请确认设备在线且使用上报正常。"
        type="info" show-icon :closable="false" style="margin-top:16px" />

      <el-row :gutter="16" style="margin-top:16px">
        <el-col :xs="24" :md="12">
          <el-card shadow="hover"><template #header>分类占比</template>
            <v-chart v-if="daily.categories.length" :option="dailyPieOption" autoresize style="height:300px" />
            <el-empty v-else description="暂无分类数据" :image-size="60" />
          </el-card>
        </el-col>
        <el-col :xs="24" :md="12">
          <el-card shadow="hover"><template #header>Top 5 应用（按时长）</template>
            <v-chart v-if="daily.topApps.length" :option="dailyTopAppsOption" autoresize style="height:300px" />
            <el-empty v-else description="暂无应用数据" :image-size="60" />
          </el-card>
        </el-col>
      </el-row>

      <el-row :gutter="16" style="margin-top:16px">
        <el-col :xs="24" :md="12">
          <el-card shadow="hover"><template #header>按时段分布</template>
            <v-chart :option="hourlyBarOption" autoresize style="height:300px" />
          </el-card>
        </el-col>
        <el-col :xs="24" :md="12">
          <el-card shadow="hover">
            <template #header>拦截记录（{{ daily.blockCount }} 次）</template>
            <el-timeline v-if="daily.events.length" class="block-timeline">
              <el-timeline-item v-for="(e, i) in daily.events" :key="`${e.time}-${i}`" :timestamp="e.time" type="danger">
                {{ e.appName }} · {{ e.category }}
              </el-timeline-item>
            </el-timeline>
            <el-empty v-else description="今日无拦截记录" :image-size="60" />
          </el-card>
        </el-col>
      </el-row>
    </template>

    <!-- ==================== 周报 ==================== -->
    <template v-if="reportType==='weekly' && weekly">
      <el-card shadow="hover" class="report-summary">
        <div class="summary-stat">
          <span class="summary-label">{{ weekly.weekStart }} ~ {{ weekly.weekEnd }} · 总使用时长</span>
          <span class="summary-value">{{ weekly.totalMinutes }} <small>分钟</small></span>
        </div>
        <div class="summary-detail">
          <span>日均 {{ Math.round(weekly.totalMinutes / 7) }} min</span>
          <span>最高 {{ Math.max(...(weekly.dailyTotals.length ? weekly.dailyTotals : [0])) }} min</span>
          <span>最低 {{ Math.min(...(weekly.dailyTotals.length ? weekly.dailyTotals : [0])) }} min</span>
          <span v-if="weekly.limitMinutes > 0">每周参考额度 {{ weekly.limitMinutes * 7 }} min</span>
          <!-- 环比上周 -->
          <span v-if="weekOverWeek" class="wow" :class="weekOverWeek.delta >= 0 ? 'wow-up' : 'wow-down'">
            环比上周 {{ weekOverWeek.delta >= 0 ? '+' : '-' }}{{ fmt(Math.abs(weekOverWeek.delta)) }}（{{ weekOverWeek.pct }}%）
          </span>
          <span v-else class="wow">上周暂无数据，无法环比</span>
        </div>
        <div v-if="weekly.categories.length" class="summary-detail" style="margin-top:8px">
          <span v-for="c in weekly.categories" :key="c.key" class="cat-chip">{{ c.name }} {{ c.minutes }}min（{{ c.percent }}%）</span>
        </div>
      </el-card>

      <el-alert v-if="weekly.totalMinutes === 0" title="本周暂无使用记录，图表为空；请确认设备在线且使用上报正常。"
        type="info" show-icon :closable="false" style="margin-top:16px" />

      <el-card shadow="hover" style="margin-top:16px"><template #header>周使用趋势</template>
        <v-chart :option="weeklyLineOption" autoresize style="height:320px" />
      </el-card>

      <el-card shadow="hover" style="margin-top:16px"><template #header>每日明细（对照每日限额）</template>
        <!-- [TASK-PRELAUNCH-P1] 移动端：表格降级为卡片列表 -->
        <div v-if="isMobile" class="daily-mobile-list">
          <div v-for="d in weekly.dailyDetails" :key="d.date" class="daily-mobile-item">
            <div class="dm-head">
              <span class="dm-date">{{ d.date.slice(5) }}</span>
              <el-tag :type="dayTagType(d.totalMinutes)" size="small">{{ dayTagText(d.totalMinutes) }}</el-tag>
            </div>
            <el-progress :percentage="dayPercent(d.totalMinutes)" :stroke-width="12">
              <span>{{ d.totalMinutes }} 分钟</span>
            </el-progress>
            <span class="dm-blocks" v-if="d.blockCount > 0">拦截 {{ d.blockCount }} 次</span>
          </div>
        </div>
        <el-table v-else :data="weekly.dailyDetails" stripe size="small">
          <el-table-column prop="date" label="日期" width="140" />
          <el-table-column label="使用时长" min-width="200">
            <template #default="{row}">
              <el-progress :percentage="dayPercent(row.totalMinutes)" :stroke-width="14">
                <span>{{ row.totalMinutes }} 分钟</span>
              </el-progress>
            </template>
          </el-table-column>
          <el-table-column label="拦截" width="90">
            <template #default="{row}">{{ row.blockCount }} 次</template>
          </el-table-column>
          <el-table-column label="状态" width="100">
            <template #default="{row}">
              <el-tag :type="dayTagType(row.totalMinutes)" size="small">{{ dayTagText(row.totalMinutes) }}</el-tag>
            </template>
          </el-table-column>
        </el-table>
      </el-card>

      <!-- [TASK-PRELAUNCH-P2] 儿童阅读版：给孩子看的周总结 -->
      <el-card shadow="hover" class="child-card" style="margin-top:16px">
        <template #header>🌟 给孩子的本周小结</template>
        <p class="child-text">{{ weeklyChildText }}</p>
      </el-card>
    </template>
  </div>
</template>

<style scoped>
.reports-page { max-width: 1200px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; flex-wrap: wrap; gap: 12px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.page-actions { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.report-summary { margin-bottom: 0; }
.summary-stat { display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 10px; }
.summary-label { font-size: 14px; color: var(--el-text-color-secondary); }
.summary-value { font-size: 28px; font-weight: 700; color: var(--el-color-primary); }
.summary-value small { font-size: 14px; font-weight: 400; }
.summary-sub { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 8px; font-size: 13px; color: var(--el-text-color-secondary); }
.quota-line b { color: var(--el-color-success); }
.quota-exhausted { color: var(--el-color-danger) !important; }
.daily-comment { margin: 6px 0; font-size: 14px; color: var(--el-text-color-primary); }
.summary-detail { display: flex; flex-wrap: wrap; gap: 8px; font-size: 13px; color: var(--el-text-color-secondary); }
.cat-chip { padding: 2px 8px; border: 1px solid var(--el-border-color); border-radius: 12px; font-size: 12px; }
.block-timeline { padding-left: 4px; max-height: 300px; overflow-y: auto; }
.wow { font-weight: 600; }
.wow-up { color: var(--el-color-danger); }
.wow-down { color: var(--el-color-success); }

/* 儿童阅读版：暖色调卡片 */
.child-card { background: linear-gradient(135deg, #fffbe8, #fff); }
.child-text { margin: 0; font-size: 15px; line-height: 1.9; color: #5c4a1e; }

/* [TASK-PRELAUNCH-P1] 移动端：明细卡片 + 页头堆叠 */
.daily-mobile-list { display: flex; flex-direction: column; gap: 10px; }
.daily-mobile-item { padding: 10px 12px; border: 1px solid var(--el-border-color-lighter); border-radius: 8px; }
.dm-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 6px; }
.dm-date { font-size: 13px; font-weight: 600; }
.dm-blocks { font-size: 12px; color: var(--el-text-color-secondary); }

@media (max-width: 768px) {
  .page-actions { width: 100%; }
  .page-actions > * { flex: 1; min-width: 0; }
  .summary-stat { flex-direction: column; gap: 4px; }
  .summary-value { font-size: 24px; }
}
</style>
