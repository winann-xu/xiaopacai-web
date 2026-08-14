<script setup lang="ts">
// 小趴菜 Web 3.0 — 策略配置
import { ref, onMounted, computed, watch } from 'vue'
import { useDeviceStore } from '@/stores/devices'
import { usePolicyStore, type Policy } from '@/stores/policies'
import { ElMessage } from 'element-plus'
import { Promotion } from '@element-plus/icons-vue'

const deviceStore = useDeviceStore()
const policyStore = usePolicyStore()
const selectedDeviceId = ref<number | null>(null)
const isDirty = ref(false)
const dailyLimit = ref(180)
const bedtimeStart = ref('21:00')
const bedtimeEnd = ref('07:00')
const timeoutAction = ref<'full_lock' | 'partial_lock' | 'warn_only'>('full_lock')
const categoryLimits = ref([
  { category: 'game' as const, label: '游戏', minutes: 0, enabled: true },
  { category: 'social' as const, label: '社交', minutes: 60, enabled: true },
  { category: 'video' as const, label: '视频', minutes: 90, enabled: true },
  { category: 'study' as const, label: '学习', minutes: 0, enabled: false },
])
const whitelistText = ref('')
const blacklistText = ref('')

const selectedDevice = computed(() => deviceStore.devices.find(d => d.id === selectedDeviceId.value))
const deviceOptions = computed(() => deviceStore.devices.map(d => ({ value: d.id, label: `${d.name} (${d.deviceId})` })))

watch(selectedDeviceId, async (id) => {
  if (!id) return
  await policyStore.fetchPolicy(id)
  const p = policyStore.getPolicy(id)
  if (p) {
    dailyLimit.value = p.dailyLimitMinutes
    bedtimeStart.value = p.bedtimeStart
    bedtimeEnd.value = p.bedtimeEnd
    timeoutAction.value = p.timeoutAction
    categoryLimits.value = p.categoryLimits.map(c => ({ ...c }))
    whitelistText.value = p.whitelist.join('\n')
    blacklistText.value = p.blacklist.join('\n')
  }
  isDirty.value = false
})

onMounted(() => {
  deviceStore.fetchDevices()
  if (deviceStore.devices.length) selectedDeviceId.value = deviceStore.devices[0].id
})

function collectPolicy(): Policy {
  return {
    deviceId: selectedDeviceId.value!,
    dailyLimitMinutes: dailyLimit.value,
    bedtimeStart: bedtimeStart.value,
    bedtimeEnd: bedtimeEnd.value,
    // [TASK-PRELAUNCH-P1] 分类限额暂不可用：不随保存提交（后端强制 -1 不限）
    categoryLimits: [],
    whitelist: whitelistText.value.split('\n').filter(Boolean),
    blacklist: blacklistText.value.split('\n').filter(Boolean),
    timeoutAction: timeoutAction.value,
  }
}

async function savePolicy() {
  if (!selectedDeviceId.value) { ElMessage.warning('请先选择设备'); return }
  try {
    await policyStore.savePolicy(selectedDeviceId.value, collectPolicy())
    isDirty.value = false
    ElMessage.success('策略已保存并下发')
  } catch { ElMessage.error('保存失败') }
}
</script>

<template>
  <div class="policies-page">
    <div class="page-header">
      <h2 class="page-title">策略配置</h2>
      <div class="page-actions">
        <el-select v-model="selectedDeviceId" placeholder="选择设备" style="width: 260px">
          <el-option v-for="d in deviceOptions" :key="d.value" :label="d.label" :value="d.value" />
        </el-select>
        <el-button type="primary" :icon="Promotion" :disabled="!isDirty" :loading="policyStore.saving" @click="savePolicy">保存并下发</el-button>
      </div>
    </div>

    <template v-if="selectedDevice">
      <el-alert v-if="selectedDevice.status !== 'online'" :title="`设备「${selectedDevice.name}」当前离线，策略将在设备上线后自动下发`"
        type="warning" show-icon :closable="false" style="margin-bottom:16px" />

      <div class="policy-grid">
        <el-card shadow="hover"><template #header>每日使用限额</template>
          <div class="slider-block">
            <el-slider v-model="dailyLimit" :min="30" :max="480" :step="10" show-input
              :marks="{ 30:'30min', 120:'2h', 240:'4h', 480:'8h' }" @change="isDirty = true" />
            <p class="hint">当前：每天 {{ dailyLimit }} 分钟 ({{ Math.floor(dailyLimit/60) }}h {{ dailyLimit%60 }}min)</p>
          </div>
        </el-card>

        <el-card shadow="hover"><template #header>就寝时段</template>
          <div class="time-range">
            <el-time-picker v-model="bedtimeStart" format="HH:mm" placeholder="开始" @change="isDirty = true" />
            <span class="time-sep">至</span>
            <el-time-picker v-model="bedtimeEnd" format="HH:mm" placeholder="结束" @change="isDirty = true" />
          </div>
          <p class="hint">就寝时段内设备将自动锁定</p>
        </el-card>

        <el-card shadow="hover" class="card-disabled">
          <template #header>
            分类限额
            <!-- [TASK-PRELAUNCH-P1] 分类限额暂不可用：仅展示，不可编辑、不随保存下发 -->
            <el-tag type="warning" size="small" effect="light" class="unavailable-tag">暂不可用</el-tag>
          </template>
          <div class="category-limits">
            <div v-for="cat in categoryLimits" :key="cat.category" class="cat-row">
              <div class="cat-info">
                <el-switch v-model="cat.enabled" size="small" disabled />
                <span class="cat-label">{{ cat.label }}</span>
              </div>
              <el-input-number v-model="cat.minutes" :min="0" :max="480" :step="10"
                size="small" style="width:120px" disabled />
              <span class="cat-unit">分钟/天</span>
            </div>
          </div>
          <p class="hint">分类限额功能开发中，暂不可用，敬请期待；请使用每日使用限额与黑白名单。</p>
        </el-card>

        <el-card shadow="hover"><template #header>应用黑白名单</template>
          <el-row :gutter="16">
            <el-col :xs="24" :span="12">
              <p class="list-label">白名单（始终允许）</p>
              <el-input v-model="whitelistText" type="textarea" :rows="4" placeholder="每行一个应用包名" @change="isDirty = true" />
            </el-col>
            <el-col :xs="24" :span="12">
              <p class="list-label">黑名单（始终禁止）</p>
              <el-input v-model="blacklistText" type="textarea" :rows="4" placeholder="每行一个应用包名" @change="isDirty = true" />
            </el-col>
          </el-row>
        </el-card>

        <el-card shadow="hover"><template #header>超时处理方式</template>
          <el-radio-group v-model="timeoutAction" @change="isDirty = true">
            <el-radio value="full_lock">整机停用</el-radio>
            <el-radio value="partial_lock">仅停用受限应用</el-radio>
            <el-radio value="warn_only">仅提醒</el-radio>
          </el-radio-group>
          <p class="hint">{{
            timeoutAction === 'full_lock' ? '超时后设备完全锁定，仅允许紧急通话' :
            timeoutAction === 'partial_lock' ? '超时后仅停用游戏/社交/视频分类，学习类不受影响' :
            '超时后仅弹出提醒，不限制使用'
          }}</p>
        </el-card>
      </div>
    </template>
    <el-empty v-else description="请先选择一个设备" :image-size="120" />
  </div>
</template>

<style scoped>
.policies-page { max-width: 1200px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; flex-wrap: wrap; gap: 12px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.page-actions { display: flex; gap: 8px; align-items: center; }
.policy-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(420px, 1fr)); gap: 16px; }
.slider-block { padding: 0 8px; }
.hint { font-size: 12px; color: var(--el-text-color-secondary); margin: 8px 0 0; }
.time-range { display: flex; align-items: center; gap: 12px; }
.time-sep { color: var(--el-text-color-secondary); }
.category-limits { display: flex; flex-direction: column; gap: 10px; }
.cat-row { display: flex; align-items: center; gap: 10px; }
.cat-info { display: flex; align-items: center; gap: 6px; width: 80px; }
.cat-label { font-size: 14px; font-weight: 500; }
.cat-unit { font-size: 12px; color: var(--el-text-color-secondary); }
.list-label { font-size: 13px; font-weight: 500; margin: 0 0 6px; }
/* 暂不可用卡片：降饱和提示 */
.card-disabled { opacity: 0.85; }
.unavailable-tag { margin-left: 8px; }
@media (max-width: 768px) {
  .policy-grid { grid-template-columns: 1fr; }
  /* 移动端：页头堆叠、选择器全宽、按钮触控区 */
  .page-header { flex-direction: column; align-items: stretch; }
  .page-actions { flex-direction: column; }
  .page-actions .el-select { width: 100% !important; }
  .page-actions .el-button { min-height: 44px; }
  .time-range { flex-wrap: wrap; }
  .cat-row { flex-wrap: wrap; }
  .cat-unit { margin-left: auto; }
}
</style>
