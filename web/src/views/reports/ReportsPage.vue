<script setup lang="ts">
// 小趴菜 Web 3.0 — 使用报告
import { ref, reactive, computed, onMounted } from 'vue'
import { useDeviceStore } from '@/stores/devices'
import { useIsMobile } from '@/composables/useIsMobile'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { LineChart, BarChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, LegendComponent, TitleComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { Download } from '@element-plus/icons-vue'
import dayjs from 'dayjs'

use([LineChart, BarChart, GridComponent, TooltipComponent, LegendComponent, TitleComponent, CanvasRenderer])

const deviceStore = useDeviceStore()
const isMobile = useIsMobile()
const reportType = ref<'daily' | 'weekly'>('daily')
const selectedDeviceId = ref<number | null>(null)

const deviceOptions = computed(() => [
  { value: null, label: '全部设备' },
  ...deviceStore.devices.map(d => ({ value: d.id, label: d.name })),
])

// Mock 数据
const dailyData = reactive({
  date: dayjs().format('YYYY-MM-DD'), totalMinutes: 132,
  categories: [{ name: '学习', minutes: 45 }, { name: '视频', minutes: 38 }, { name: '社交', minutes: 30 }, { name: '游戏', minutes: 19 }],
  hourlyData: [5,8,0,0,0,3,12,20,28,15,18,22,5,1,8,12,10,0,18,26,20,12,8,0],
})
const weeklyData = reactive({
  weekStart: dayjs().subtract(6,'day').format('YYYY-MM-DD'), weekEnd: dayjs().format('YYYY-MM-DD'), totalMinutes: 890,
  dailyTotals: [145,132,98,156,110,180,69],
  dates: Array.from({length:7},(_,i)=>dayjs().subtract(6-i,'day').format('MM/DD')),
})

const categoryPieOption = computed(() => ({
  tooltip: { trigger: 'item' as const }, legend: { bottom: 0 },
  series: [{ name: '分类占比', type: 'pie' as const, radius: ['40%','68%'],
    data: dailyData.categories.map(c=>({name:c.name,value:c.minutes})),
    label: { formatter: '{b}: {c}min' },
  }],
}))

const hourlyBarOption = computed(() => ({
  tooltip: { trigger: 'axis' as const },
  xAxis: { type: 'category' as const, data: Array.from({length:24},(_,i)=>`${i}:00`), axisLabel: { rotate: 45, fontSize: 10 } },
  yAxis: { type: 'value' as const, name: '分钟' },
  series: [{ name: '使用时长', type: 'bar' as const, data: dailyData.hourlyData, itemStyle: { color: '#409EFF' } }],
  grid: { left: 50, right: 20, top: 20, bottom: 50 },
}))

const weeklyLineOption = computed(() => ({
  tooltip: { trigger: 'axis' as const },
  xAxis: { type: 'category' as const, data: weeklyData.dates },
  yAxis: { type: 'value' as const, name: '分钟' },
  series: [{ name: '每日使用', type: 'line' as const, data: weeklyData.dailyTotals, smooth: true, areaStyle: { opacity: .15 } }],
  grid: { left: 50, right: 20, top: 20, bottom: 40 },
}))

onMounted(() => { deviceStore.fetchDevices() })

function exportReport(format: 'txt'|'json'|'csv') {
  const data = reportType.value === 'daily' ? dailyData : weeklyData
  let content = '', filename = '', mime = ''
  if (format === 'json') { content = JSON.stringify(data,null,2); filename=`report-${reportType.value}-${dayjs().format('YYYYMMDD')}.json`; mime='application/json' }
  else if (format === 'csv') { content = 'Name,Minutes\n'+dailyData.categories.map(c=>`${c.name},${c.minutes}`).join('\n'); filename=`report-${reportType.value}-${dayjs().format('YYYYMMDD')}.csv`; mime='text/csv' }
  else { content = JSON.stringify(data,null,2); filename=`report-${reportType.value}-${dayjs().format('YYYYMMDD')}.txt`; mime='text/plain' }
  const blob = new Blob([content],{type:mime}); const url = URL.createObjectURL(blob)
  const a = document.createElement('a'); a.href=url; a.download=filename; a.click(); URL.revokeObjectURL(url)
}
</script>

<template>
  <div class="reports-page">
    <div class="page-header">
      <h2 class="page-title">使用报告</h2>
      <div class="page-actions">
        <el-select v-model="selectedDeviceId" placeholder="设备" clearable style="width:140px">
          <el-option v-for="d in deviceOptions" :key="String(d.value)" :label="d.label" :value="d.value" />
        </el-select>
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

    <template v-if="reportType==='daily'">
      <el-card shadow="hover" class="report-summary">
        <div class="summary-stat"><span class="summary-label">{{ dailyData.date }} 总使用时长</span><span class="summary-value">{{ dailyData.totalMinutes }} <small>分钟</small></span></div>
        <div class="summary-detail"><span v-for="c in dailyData.categories" :key="c.name" class="cat-chip">{{ c.name }} {{ c.minutes }}min</span></div>
      </el-card>
      <el-row :gutter="16" style="margin-top:16px">
        <el-col :xs="24" :md="12"><el-card shadow="hover"><template #header>分类占比</template><v-chart :option="categoryPieOption" autoresize style="height:300px" /></el-card></el-col>
        <el-col :xs="24" :md="12"><el-card shadow="hover"><template #header>按时段分布</template><v-chart :option="hourlyBarOption" autoresize style="height:300px" /></el-card></el-col>
      </el-row>
    </template>

    <template v-if="reportType==='weekly'">
      <el-card shadow="hover" class="report-summary">
        <div class="summary-stat"><span class="summary-label">{{ weeklyData.weekStart }} ~ {{ weeklyData.weekEnd }}</span><span class="summary-value">{{ weeklyData.totalMinutes }} <small>分钟</small></span></div>
        <div class="summary-detail"><span>日均 {{ Math.round(weeklyData.totalMinutes/7) }} min</span><span style="margin-left:12px">最高 {{ Math.max(...weeklyData.dailyTotals) }} min</span><span style="margin-left:12px">最低 {{ Math.min(...weeklyData.dailyTotals) }} min</span></div>
      </el-card>
      <el-card shadow="hover" style="margin-top:16px"><template #header>周使用趋势</template><v-chart :option="weeklyLineOption" autoresize style="height:320px" /></el-card>
      <el-card shadow="hover" style="margin-top:16px"><template #header>每日明细</template>
        <!-- [TASK-PRELAUNCH-P1] 移动端：表格降级为卡片列表 -->
        <div v-if="isMobile" class="daily-mobile-list">
          <div v-for="(d, i) in weeklyData.dates" :key="d" class="daily-mobile-item">
            <div class="dm-head">
              <span class="dm-date">{{ d }}</span>
              <el-tag :type="weeklyData.dailyTotals[i]>150?'danger':weeklyData.dailyTotals[i]>100?'warning':'success'" size="small">
                {{ weeklyData.dailyTotals[i]>150?'超标':weeklyData.dailyTotals[i]>100?'正常':'偏低' }}
              </el-tag>
            </div>
            <el-progress :percentage="Math.round(weeklyData.dailyTotals[i]/200*100)" :stroke-width="12">
              <span>{{ weeklyData.dailyTotals[i] }} 分钟</span>
            </el-progress>
          </div>
        </div>
        <el-table v-else :data="weeklyData.dates.map((d,i)=>({date:d,minutes:weeklyData.dailyTotals[i]}))" stripe size="small">
          <el-table-column prop="date" label="日期" width="140" />
          <el-table-column label="使用时长" min-width="200">
            <template #default="{row}"><el-progress :percentage="Math.round(row.minutes/200*100)" :stroke-width="14"><span>{{ row.minutes }} 分钟</span></el-progress></template>
          </el-table-column>
          <el-table-column label="状态" width="100">
            <template #default="{row}"><el-tag :type="row.minutes>150?'danger':row.minutes>100?'warning':'success'" size="small">{{ row.minutes>150?'超标':row.minutes>100?'正常':'偏低' }}</el-tag></template>
          </el-table-column>
        </el-table>
      </el-card>
    </template>
  </div>
</template>

<style scoped>
.reports-page { max-width: 1200px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; flex-wrap: wrap; gap: 12px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.page-actions { display: flex; gap: 8px; align-items: center; }
.report-summary { margin-bottom: 0; }
.summary-stat { display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 10px; }
.summary-label { font-size: 14px; color: var(--el-text-color-secondary); }
.summary-value { font-size: 28px; font-weight: 700; color: var(--el-color-primary); }
.summary-value small { font-size: 14px; font-weight: 400; }
.summary-detail { display: flex; flex-wrap: wrap; gap: 8px; font-size: 13px; color: var(--el-text-color-secondary); }
.cat-chip { padding: 2px 8px; border: 1px solid var(--el-border-color); border-radius: 12px; font-size: 12px; }

/* [TASK-PRELAUNCH-P1] 移动端：明细卡片 + 页头堆叠 */
.daily-mobile-list { display: flex; flex-direction: column; gap: 10px; }
.daily-mobile-item { padding: 10px 12px; border: 1px solid var(--el-border-color-lighter); border-radius: 8px; }
.dm-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 6px; }
.dm-date { font-size: 13px; font-weight: 600; }

@media (max-width: 768px) {
  .page-actions { flex-wrap: wrap; width: 100%; }
  .page-actions > * { flex: 1; min-width: 0; }
  .summary-stat { flex-direction: column; gap: 4px; }
  .summary-value { font-size: 24px; }
}
</style>
