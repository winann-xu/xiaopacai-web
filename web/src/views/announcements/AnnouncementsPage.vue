<script setup lang="ts">
// 小趴菜 Web 3.0 — 公告管理
import { ref, onMounted, computed } from 'vue'
import { useAnnouncementStore, type Announcement } from '@/stores/announcements'
import { useIsMobile } from '@/composables/useIsMobile'
import { announcementApi } from '@/api'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Edit, Delete, Promotion, Remove, Search } from '@element-plus/icons-vue'

const announcementStore = useAnnouncementStore()
const isMobile = useIsMobile()
const showEditor = ref(false)
const editingId = ref<number | null>(null)
const editForm = ref({ title: '', content: '', priority: 'normal' as 'normal'|'important'|'urgent', validUntil: '' })
const filterStatus = ref<string>('all')
const filterPriority = ref<string>('all')
const searchText = ref('')

// [TASK-PRELAUNCH-P3] 送达与回执：按公告查看每设备推送/显示/确认时间（见 docs/adr/0004）
interface DeliveryRow {
  deviceId: number; deviceName: string; pushCount: number
  lastPushedAt: string | null; displayedAt: string | null; acknowledgedAt: string | null
}
const deliveriesVisible = ref(false)
const deliveriesLoading = ref(false)
const deliveriesAnnTitle = ref('')
const deliveriesRows = ref<DeliveryRow[]>([])

async function openDeliveries(ann: Announcement) {
  deliveriesVisible.value = true
  deliveriesAnnTitle.value = ann.title
  deliveriesRows.value = []
  deliveriesLoading.value = true
  try {
    const res = await announcementApi.deliveries(ann.id)
    deliveriesRows.value = res.data.deliveries ?? []
  } catch { ElMessage.error('回执加载失败') } finally { deliveriesLoading.value = false }
}

const fmtTime = (t: string | null) => t ? new Date(t).toLocaleString('zh-CN', { hour12: false }) : '—'
const ackTag = (t: string | null) => t ? 'success' : 'info'
const ackText = (t: string | null) => t ? '已确认' : '未确认'

const filteredAnnouncements = computed(() => {
  let list = announcementStore.announcements
  if (filterStatus.value !== 'all') list = list.filter(a => a.status === filterStatus.value)
  if (filterPriority.value !== 'all') list = list.filter(a => a.priority === filterPriority.value)
  const q = searchText.value.trim().toLowerCase()
  if (q) {
    list = list.filter(a =>
      a.title.toLowerCase().includes(q) ||
      a.content.toLowerCase().includes(q) ||
      (a.creatorAccount || '').toLowerCase().includes(q))
  }
  return list
})

onMounted(async () => {
  try { await announcementStore.fetchAnnouncements() }
  catch { ElMessage.error('公告加载失败，请刷新重试') }
})

function openCreate() {
  editingId.value = null
  editForm.value = { title: '', content: '', priority: 'normal', validUntil: '' }
  showEditor.value = true
}

function openEdit(ann: Announcement) {
  editingId.value = ann.id
  editForm.value = { title: ann.title, content: ann.content, priority: ann.priority, validUntil: ann.validUntil ? new Date(ann.validUntil).toISOString().slice(0, 16) : '' }
  showEditor.value = true
}

async function saveAnnouncement() {
  if (!editForm.value.title.trim()) { ElMessage.warning('请输入公告标题'); return }
  const data = { ...editForm.value, validUntil: editForm.value.validUntil ? new Date(editForm.value.validUntil).toISOString() : new Date(Date.now() + 7*86400000).toISOString() }
  try {
    if (editingId.value) { await announcementStore.updateAnnouncement(editingId.value, data); ElMessage.success('已更新') }
    else { await announcementStore.createAnnouncement(data); ElMessage.success('已创建') }
    showEditor.value = false
  } catch { ElMessage.error('保存失败') }
}

async function handlePublish(ann: Announcement) { await announcementStore.publishAnnouncement(ann.id); ElMessage.success('已发布') }
async function handleRevoke(ann: Announcement) {
  try { await ElMessageBox.confirm('确定要撤回该公告吗？', '确认撤回', { type: 'warning' }); await announcementStore.revokeAnnouncement(ann.id); ElMessage.success('已撤回') } catch { /* */ }
}
async function handleDelete(ann: Announcement) {
  try { await ElMessageBox.confirm('确定要删除该公告吗？', '确认删除', { type: 'warning' }); await announcementStore.deleteAnnouncement(ann.id); ElMessage.success('已删除') } catch { /* */ }
}

function pTag(p: string) { return p==='urgent'?'danger':p==='important'?'warning':'info' }
function pText(p: string) { return p==='urgent'?'紧急':p==='important'?'重要':'普通' }
function sTag(s: string) { return s==='published'?'success':s==='draft'?'info':'warning' }
function sText(s: string) { return s==='published'?'已发布':s==='draft'?'草稿':'已撤回' }
</script>

<template>
  <div class="ann-page">
    <div class="page-header">
      <h2 class="page-title">公告管理</h2>
      <div class="page-actions">
        <el-input v-model="searchText" placeholder="搜索标题/内容/账号" :prefix-icon="Search" clearable class="ann-search" />
        <el-select v-model="filterStatus" style="width:100px" size="small">
          <el-option label="全部" value="all"/><el-option label="已发布" value="published"/>
          <el-option label="草稿" value="draft"/><el-option label="已撤回" value="revoked"/>
        </el-select>
        <el-select v-model="filterPriority" style="width:100px" size="small">
          <el-option label="全部等级" value="all"/><el-option label="普通" value="normal"/>
          <el-option label="重要" value="important"/><el-option label="紧急" value="urgent"/>
        </el-select>
        <el-button type="primary" :icon="Plus" @click="openCreate">新建公告</el-button>
      </div>
    </div>

    <div v-loading="announcementStore.loading" class="ann-list">
      <el-empty v-if="!filteredAnnouncements.length" description="暂无公告" />
      <el-card v-for="ann in filteredAnnouncements" :key="ann.id" shadow="hover" class="ann-card"
        :class="{ 'is-draft': ann.status==='draft', 'is-revoked': ann.status==='revoked' }">
        <div class="ann-header">
          <div class="ann-title-row">
            <h3 class="ann-title">{{ ann.title }}</h3>
            <el-tag :type="pTag(ann.priority)" size="small" effect="dark">{{ pText(ann.priority) }}</el-tag>
            <el-tag :type="sTag(ann.status)" size="small">{{ sText(ann.status) }}</el-tag>
          </div>
          <div class="ann-meta">
            <span>创建：{{ new Date(ann.createdAt).toLocaleString('zh-CN') }}</span>
            <span v-if="ann.creatorAccount"> · 账号：{{ ann.creatorAccount }}</span>
            <span v-if="ann.publishedAt"> · 发布：{{ new Date(ann.publishedAt).toLocaleString('zh-CN') }}</span>
            <span> · 有效期至：{{ new Date(ann.validUntil).toLocaleString('zh-CN') }}</span>
          </div>
        </div>
        <div class="ann-content">{{ ann.content }}</div>
        <div class="ann-actions">
          <template v-if="ann.status==='draft'">
            <el-button size="small" :icon="Edit" text type="primary" @click="openEdit(ann)">编辑</el-button>
            <el-button size="small" :icon="Promotion" text type="success" @click="handlePublish(ann)">发布</el-button>
            <el-button size="small" :icon="Delete" text type="danger" @click="handleDelete(ann)">删除</el-button>
          </template>
          <template v-else-if="ann.status==='published'">
            <el-button size="small" :icon="Remove" text type="warning" @click="handleRevoke(ann)">撤回</el-button>
            <el-button size="small" text type="primary" @click="openDeliveries(ann)">送达回执</el-button>
            <el-button size="small" :icon="Delete" text type="danger" @click="handleDelete(ann)">删除</el-button>
          </template>
          <template v-else-if="ann.status==='revoked'">
            <el-button size="small" :icon="Edit" text type="primary" @click="openEdit(ann)">编辑</el-button>
            <el-button size="small" :icon="Promotion" text type="success" @click="handlePublish(ann)">重新发布</el-button>
            <el-button size="small" text type="primary" @click="openDeliveries(ann)">送达回执</el-button>
            <el-button size="small" :icon="Delete" text type="danger" @click="handleDelete(ann)">删除</el-button>
          </template>
        </div>
      </el-card>
    </div>

    <!-- [TASK-PRELAUNCH-P3] 送达与回执弹窗：移动端表格降级卡片 -->
    <el-dialog v-model="deliveriesVisible" :title="`送达与回执 — ${deliveriesAnnTitle}`" width="720px">
      <div v-loading="deliveriesLoading">
        <el-empty v-if="!deliveriesRows.length && !deliveriesLoading" description="暂无送达记录（设备在线后推送/重连补推时产生）" :image-size="80" />
        <template v-else>
          <div v-if="isMobile" class="del-mobile-list">
            <div v-for="d in deliveriesRows" :key="d.deviceId" class="del-mobile-item">
              <div class="dm-head">
                <span class="dm-name">{{ d.deviceName }}</span>
                <el-tag :type="ackTag(d.acknowledgedAt)" size="small">{{ ackText(d.acknowledgedAt) }}</el-tag>
              </div>
              <p class="dm-line">推送 {{ d.pushCount }} 次 · 最近 {{ fmtTime(d.lastPushedAt) }}</p>
              <p class="dm-line">显示 {{ fmtTime(d.displayedAt) }} · 确认 {{ fmtTime(d.acknowledgedAt) }}</p>
            </div>
          </div>
          <el-table v-else :data="deliveriesRows" stripe size="small">
            <el-table-column prop="deviceName" label="设备" min-width="120" />
            <el-table-column label="推送次数" width="90">
              <template #default="{row}">{{ row.pushCount }} 次</template>
            </el-table-column>
            <el-table-column label="最近推送" min-width="150">
              <template #default="{row}">{{ fmtTime(row.lastPushedAt) }}</template>
            </el-table-column>
            <el-table-column label="终端显示" min-width="150">
              <template #default="{row}">{{ fmtTime(row.displayedAt) }}</template>
            </el-table-column>
            <el-table-column label="确认状态" width="150">
              <template #default="{row}">
                <span>{{ fmtTime(row.acknowledgedAt) }}</span>
                <el-tag :type="ackTag(row.acknowledgedAt)" size="small" style="margin-left:6px">{{ ackText(row.acknowledgedAt) }}</el-tag>
              </template>
            </el-table-column>
          </el-table>
        </template>
      </div>
    </el-dialog>

    <el-dialog v-model="showEditor" :title="editingId?'编辑公告':'新建公告'" width="600px">
      <el-form label-position="top">
        <el-form-item label="标题" required><el-input v-model="editForm.title" placeholder="公告标题" maxlength="100" show-word-limit /></el-form-item>
        <el-form-item label="内容" required><el-input v-model="editForm.content" type="textarea" :rows="5" placeholder="公告内容" maxlength="500" show-word-limit /></el-form-item>
        <el-row :gutter="16">
          <el-col :xs="24" :span="12">
            <el-form-item label="优先级">
              <el-select v-model="editForm.priority" style="width:100%">
                <el-option label="普通" value="normal"/><el-option label="重要" value="important"/><el-option label="紧急" value="urgent"/>
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :xs="24" :span="12">
            <el-form-item label="有效期至">
              <el-date-picker v-model="editForm.validUntil" type="datetime" placeholder="选择时间" style="width:100%" />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>
      <template #footer>
        <el-button @click="showEditor=false">取消</el-button>
        <el-button type="primary" @click="saveAnnouncement">{{ editingId?'保存':'创建' }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.ann-page { max-width: 1000px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; flex-wrap: wrap; gap: 12px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.page-actions { display: flex; gap: 8px; align-items: center; }
.ann-search { width: 220px; }
.ann-list { display: flex; flex-direction: column; gap: 14px; }
.ann-card { transition: transform .2s; }
.ann-card:hover { transform: translateY(-1px); }
.ann-card.is-draft { border-left: 3px solid var(--el-color-info); }
.ann-card.is-revoked { opacity: .65; }
.ann-header { margin-bottom: 10px; }
.ann-title-row { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }
.ann-title { font-size: 16px; font-weight: 600; margin: 0; }
.ann-meta { font-size: 12px; color: var(--el-text-color-placeholder); }
.ann-content { font-size: 14px; color: var(--el-text-color-regular); line-height: 1.6; white-space: pre-wrap; }
.ann-actions { display: flex; gap: 4px; margin-top: 12px; padding-top: 10px; border-top: 1px solid var(--el-border-color-lighter); }
/* [TASK-PRELAUNCH-P3] 回执移动端卡片 */
.del-mobile-list { display: flex; flex-direction: column; gap: 10px; }
.del-mobile-item { padding: 10px 12px; border: 1px solid var(--el-border-color-lighter); border-radius: 8px; }
.dm-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px; }
.dm-name { font-size: 13px; font-weight: 600; }
.dm-line { margin: 2px 0; font-size: 12px; color: var(--el-text-color-secondary); }

/* [TASK-PRELAUNCH-P1] 移动端：页头堆叠、元信息换行、操作按钮触控区 */
@media (max-width: 768px) {
  .page-header { flex-direction: column; align-items: stretch; }
  .page-actions { display: flex; flex-wrap: wrap; }
  .ann-search { width: 100%; }
  .page-actions .el-button { min-height: 44px; }
  .ann-title-row { flex-wrap: wrap; }
  .ann-meta { display: flex; flex-direction: column; gap: 2px; }
  .ann-actions .el-button { min-height: 44px; flex: 1; }
}
</style>
