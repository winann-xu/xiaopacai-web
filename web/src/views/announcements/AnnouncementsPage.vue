<script setup lang="ts">
// 小趴菜 Web 3.0 — 公告管理
import { ref, onMounted, computed } from 'vue'
import { useAnnouncementStore, type Announcement } from '@/stores/announcements'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Edit, Delete, Promotion, Remove } from '@element-plus/icons-vue'

const announcementStore = useAnnouncementStore()
const showEditor = ref(false)
const editingId = ref<number | null>(null)
const editForm = ref({ title: '', content: '', priority: 'normal' as 'normal'|'important'|'urgent', validUntil: '' })
const filterStatus = ref<string>('all')

const filteredAnnouncements = computed(() =>
  filterStatus.value === 'all' ? announcementStore.announcements : announcementStore.announcements.filter(a => a.status === filterStatus.value))

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
        <el-select v-model="filterStatus" style="width:100px" size="small">
          <el-option label="全部" value="all"/><el-option label="已发布" value="published"/>
          <el-option label="草稿" value="draft"/><el-option label="已撤回" value="revoked"/>
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
            <el-button size="small" :icon="Delete" text type="danger" @click="handleDelete(ann)">删除</el-button>
          </template>
          <template v-else-if="ann.status==='revoked'">
            <el-button size="small" :icon="Edit" text type="primary" @click="openEdit(ann)">编辑</el-button>
            <el-button size="small" :icon="Promotion" text type="success" @click="handlePublish(ann)">重新发布</el-button>
            <el-button size="small" :icon="Delete" text type="danger" @click="handleDelete(ann)">删除</el-button>
          </template>
        </div>
      </el-card>
    </div>

    <el-dialog v-model="showEditor" :title="editingId?'编辑公告':'新建公告'" width="600px">
      <el-form label-position="top">
        <el-form-item label="标题" required><el-input v-model="editForm.title" placeholder="公告标题" maxlength="100" show-word-limit /></el-form-item>
        <el-form-item label="内容" required><el-input v-model="editForm.content" type="textarea" :rows="5" placeholder="公告内容" maxlength="500" show-word-limit /></el-form-item>
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="优先级">
              <el-select v-model="editForm.priority" style="width:100%">
                <el-option label="普通" value="normal"/><el-option label="重要" value="important"/><el-option label="紧急" value="urgent"/>
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
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
</style>
