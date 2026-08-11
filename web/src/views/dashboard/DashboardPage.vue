<script setup lang="ts">
// 小趴菜 Web 3.0 — 仪表盘
import { onMounted, computed } from 'vue'
import { useDeviceStore } from '@/stores/devices'
import { useAnnouncementStore } from '@/stores/announcements'
import { Monitor, WarningFilled, Notification, Clock } from '@element-plus/icons-vue'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { PieChart, BarChart, LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, LegendComponent, TitleComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'

use([PieChart, BarChart, LineChart, GridComponent, TooltipComponent, LegendComponent, TitleComponent, CanvasRenderer])

const deviceStore = useDeviceStore()
const announcementStore = useAnnouncementStore()

// 统计卡片数据
const stats = computed(() => ({
  totalDevices: deviceStore.totalCount,
  onlineDevices: deviceStore.onlineCount,
  totalUsageMin: deviceStore.devices.reduce((s, d) => s + d.todayUsageMinutes, 0),
  totalLimitMin: deviceStore.devices.reduce((s, d) => s + d.todayLimitMinutes, 0),
  urgentAnnouncements: announcementStore.announcements.filter(a => a.priority === 'urgent' && a.status === 'published').length,
}))

// 使用时长饼图
const usagePieOption = computed(() => ({
  tooltip: { trigger: 'item' as const },
  legend: { bottom: 0 },
  series: [{
    name: '今日使用',
    type: 'pie' as const,
    radius: ['45%', '72%'],
    avoidLabelOverlap: false,
    label: { show: false },
    data: deviceStore.devices.map(d => ({
      name: d.name,
      value: d.todayUsageMinutes,
    })),
  }],
}))

// 最近事件
const recentEvents = computed(() => {
  const events: { time: string; text: string; type: 'info' | 'warning' | 'danger' }[] = []
  deviceStore.devices.forEach(d => {
    if (d.status === 'offline') {
      events.push({ time: d.lastSeen, text: `${d.name} 已离线`, type: 'warning' })
    }
    if (d.todayUsageMinutes >= d.todayLimitMinutes) {
      events.push({ time: new Date().toISOString(), text: `${d.name} 已达今日限额`, type: 'danger' })
    }
  })
  announcementStore.announcements.filter(a => a.status === 'published').forEach(a => {
    events.push({ time: a.publishedAt || a.createdAt, text: `公告：${a.title}`, type: 'info' })
  })
  return events.slice(0, 10)
})

onMounted(async () => {
  await Promise.all([
    deviceStore.fetchDevices(),
    announcementStore.fetchAnnouncements(),
  ])
})
</script>

<template>
  <div class="dashboard-page">
    <h2 class="page-title">仪表盘</h2>

    <!-- 统计卡片 -->
    <el-row :gutter="16" class="stat-cards">
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-inner">
            <el-icon class="stat-icon online" :size="32"><Monitor /></el-icon>
            <div>
              <div class="stat-value">{{ stats.onlineDevices }}<small> / {{ stats.totalDevices }}</small></div>
              <div class="stat-label">设备在线</div>
            </div>
          </div>
        </el-card>
      </el-col>
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-inner">
            <el-icon class="stat-icon primary" :size="32"><Clock /></el-icon>
            <div>
              <div class="stat-value">{{ stats.totalUsageMin }}<small> min</small></div>
              <div class="stat-label">今日已用 / {{ stats.totalLimitMin }} min 限额</div>
            </div>
          </div>
        </el-card>
      </el-col>
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-inner">
            <el-icon class="stat-icon danger" :size="32"><WarningFilled /></el-icon>
            <div>
              <div class="stat-value">{{ deviceStore.offlineDevices.length }}<small> 台</small></div>
              <div class="stat-label">离线设备</div>
            </div>
          </div>
        </el-card>
      </el-col>
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-inner">
            <el-icon class="stat-icon warning" :size="32"><Notification /></el-icon>
            <div>
              <div class="stat-value">{{ stats.urgentAnnouncements }}<small> 条</small></div>
              <div class="stat-label">紧急公告</div>
            </div>
          </div>
        </el-card>
      </el-col>
    </el-row>

    <!-- 图表 + 事件 -->
    <el-row :gutter="16" style="margin-top: 16px">
      <el-col :xs="24" :md="14">
        <el-card shadow="hover"><template #header>今日使用分布</template>
          <v-chart :option="usagePieOption" autoresize style="height: 300px" />
        </el-card>
      </el-col>
      <el-col :xs="24" :md="10">
        <el-card shadow="hover" class="events-card"><template #header>最近事件</template>
          <el-timeline v-if="recentEvents.length">
            <el-timeline-item
              v-for="(evt, idx) in recentEvents" :key="idx"
              :timestamp="new Date(evt.time).toLocaleString('zh-CN')"
              :type="evt.type === 'danger' ? 'danger' : evt.type === 'warning' ? 'warning' : 'primary'"
              placement="top"
            >{{ evt.text }}</el-timeline-item>
          </el-timeline>
          <el-empty v-else description="暂无事件" :image-size="80" />
        </el-card>
      </el-col>
    </el-row>

    <!-- 设备快捷状态 -->
    <el-card shadow="hover" style="margin-top: 16px">
      <template #header>设备概览</template>
      <el-table :data="deviceStore.devices" v-loading="deviceStore.loading" stripe size="small">
        <el-table-column prop="name" label="设备名称" min-width="140" />
        <el-table-column prop="deviceId" label="设备ID" width="120" />
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <el-tag :type="row.status === 'online' ? 'success' : row.status === 'reconnecting' ? 'warning' : 'info'" size="small">
              {{ row.status === 'online' ? '在线' : row.status === 'reconnecting' ? '重连中' : '离线' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="今日使用" width="160">
          <template #default="{ row }">
            <el-progress :percentage="row.todayLimitMinutes ? Math.round(row.todayUsageMinutes / row.todayLimitMinutes * 100) : 0"
              :status="row.todayUsageMinutes >= row.todayLimitMinutes ? 'exception' : undefined" :stroke-width="14">
              <span>{{ row.todayUsageMinutes }} / {{ row.todayLimitMinutes }} min</span>
            </el-progress>
          </template>
        </el-table-column>
        <el-table-column prop="ipAddress" label="IP 地址" width="140" />
        <el-table-column label="最后在线" width="170">
          <template #default="{ row }">{{ new Date(row.lastSeen).toLocaleString('zh-CN') }}</template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<style scoped>
.dashboard-page { max-width: 1400px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0 0 16px; color: var(--el-text-color-primary); }
.stat-cards .el-col { margin-bottom: 16px; }
.stat-card { cursor: default; }
.stat-inner { display: flex; align-items: center; gap: 14px; }
.stat-icon { flex-shrink: 0; }
.stat-icon.online { color: var(--el-color-success); }
.stat-icon.primary { color: var(--el-color-primary); }
.stat-icon.danger { color: var(--el-color-danger); }
.stat-icon.warning { color: var(--el-color-warning); }
.stat-value { font-size: 24px; font-weight: 700; line-height: 1.2; color: var(--el-text-color-primary); }
.stat-value small { font-size: 13px; font-weight: 400; color: var(--el-text-color-secondary); }
.stat-label { font-size: 13px; color: var(--el-text-color-secondary); margin-top: 2px; }
.events-card { height: calc(300px + 58px); overflow-y: auto; }
</style>
