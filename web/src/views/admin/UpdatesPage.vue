<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：App 更新管理（[TASK-APP-UPDATE-V1]）
// 流程：创建草稿 → 按 ABI 上传 APK（服务端算 SHA-256）→ 发布并广播 update_available。
// 红线：versionCode 单调递增（服务端防降级）；发布写审计；仅 admin 可操作。
import { ref, reactive, onMounted } from 'vue'
import { updateApi } from '@/api'
import { ElMessage, ElMessageBox } from 'element-plus'

const ABIS = ['arm64-v8a', 'armeabi-v7a', 'x86_64'] as const

interface UpdateItem {
  id: number
  versionName: string
  versionCode: number
  minVersionCode: number
  sizeBytes: number
  changelog: string
  status: string
  channel: string
  publishedAt: string | null
  createdBy: number
  createdAt: string
  abiUrls: Record<string, string>
  abiSha256: Record<string, string>
}

const loading = ref(false)
const items = ref<UpdateItem[]>([])
const createVisible = ref(false)
const creating = ref(false)
const publishingId = ref<number | null>(null)
const uploadingId = ref<number | null>(null)

const createForm = reactive({
  versionName: '',
  versionCode: 0,
  minVersionCode: 0,
  changelog: '',
})

async function load() {
  loading.value = true
  try {
    const res = await updateApi.list()
    items.value = res.data as UpdateItem[]
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '加载更新清单失败')
  } finally {
    loading.value = false
  }
}

onMounted(load)

function openCreate() {
  createForm.versionName = ''
  createForm.versionCode = 0
  createForm.minVersionCode = 0
  createForm.changelog = ''
  createVisible.value = true
}

async function handleCreate() {
  if (!createForm.versionName.trim()) { ElMessage.warning('请填写版本名（如 1.2.0）'); return }
  if (createForm.versionCode <= 0) { ElMessage.warning('versionCode 必须为正整数（v1.2.0 → 10200）'); return }
  creating.value = true
  try {
    await updateApi.create(createForm)
    ElMessage.success('草稿已创建')
    createVisible.value = false
    await load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '创建失败')
  } finally {
    creating.value = false
  }
}

function onFileChange(item: UpdateItem, abi: string, event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  if (!file.name.toLowerCase().endsWith('.apk')) { ElMessage.warning('仅允许 .apk 文件'); return }
  handleUpload(item, abi, file)
  input.value = ''
}

async function handleUpload(item: UpdateItem, abi: string, file: File) {
  uploadingId.value = item.id
  try {
    const res = await updateApi.upload(item.id, abi, file)
    ElMessage.success(`${abi} 已上传（sha256: ${res.data.sha256.slice(0, 16)}…）`)
    await load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '上传失败')
  } finally {
    uploadingId.value = null
  }
}

async function handlePublish(item: UpdateItem) {
  const abiCount = Object.keys(item.abiUrls).length
  if (abiCount === 0) { ElMessage.warning('至少上传一个 ABI 的 APK 才能发布'); return }
  try {
    await ElMessageBox.confirm(
      `发布 v${item.versionName}（versionCode ${item.versionCode}）将立即广播 update_available 到全部在线设备，` +
      `低于 minVersionCode ${item.minVersionCode} 的客户端将被强制更新。发布动作会写入审计日志。确认发布？`,
      '确认发布并推送',
      { type: 'warning', confirmButtonText: '发布并推送', cancelButtonText: '取消' },
    )
  } catch {
    return
  }
  publishingId.value = item.id
  try {
    const res = await updateApi.publish(item.id)
    ElMessage.success(`已发布，推送触达 ${res.data.pushedOnline} 台在线设备`)
    await load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '发布失败')
  } finally {
    publishingId.value = null
  }
}

function fmtBytes(n: number) {
  if (!n) return '—'
  if (n >= 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`
  return `${(n / 1024).toFixed(0)} KB`
}

function fmtTime(iso: string | null | undefined) {
  return iso ? new Date(iso).toLocaleString('zh-CN') : '—'
}
</script>

<template>
  <div class="admin-page updates-page">
    <div class="page-header">
      <h2 class="page-title">App 更新管理</h2>
      <el-button type="primary" @click="openCreate">新建版本草稿</el-button>
    </div>

    <el-alert
      type="info"
      :closable="false"
      show-icon
      style="margin-bottom: 16px"
      title="发布后在线设备立即收到更新通知（离线设备启动/重连补检）；低于 minVersionCode 的客户端为强制更新，不可跳过。"
    />

    <el-card v-loading="loading" shadow="never">
      <el-table :data="items" stripe>
        <el-table-column label="版本" min-width="120">
          <template #default="{ row }: { row: UpdateItem }">
            <b>v{{ row.versionName }}</b>
            <div class="form-tip">code {{ row.versionCode }} / min {{ row.minVersionCode }}</div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="90">
          <template #default="{ row }: { row: UpdateItem }">
            <el-tag :type="row.status === 'published' ? 'success' : 'info'" size="small">
              {{ row.status === 'published' ? '已发布' : '草稿' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="更新说明" min-width="200">
          <template #default="{ row }: { row: UpdateItem }">
            <span style="white-space: pre-line">{{ row.changelog || '—' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="ABI 包" min-width="220">
          <template #default="{ row }: { row: UpdateItem }">
            <div v-for="abi in ABIS" :key="abi" class="abi-row">
              <span class="abi-name">{{ abi }}</span>
              <template v-if="row.abiUrls[abi]">
                <span class="abi-sha" :title="row.abiSha256[abi]">sha {{ (row.abiSha256[abi] || '').slice(0, 12) }}…</span>
              </template>
              <template v-else-if="row.status === 'draft'">
                <label class="upload-btn">
                  <span class="link-like">上传</span>
                  <input
                    type="file"
                    accept=".apk"
                    style="display: none"
                    :disabled="uploadingId === row.id"
                    @change="onFileChange(row, abi, $event)"
                  />
                </label>
              </template>
              <template v-else>—</template>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="大小" width="90">
          <template #default="{ row }: { row: UpdateItem }">{{ fmtBytes(row.sizeBytes) }}</template>
        </el-table-column>
        <el-table-column label="发布时间" width="160">
          <template #default="{ row }: { row: UpdateItem }">{{ fmtTime(row.publishedAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="110" fixed="right">
          <template #default="{ row }: { row: UpdateItem }">
            <el-button
              v-if="row.status === 'draft'"
              type="primary"
              size="small"
              :loading="publishingId === row.id"
              @click="handlePublish(row)"
            >
              发布并推送
            </el-button>
            <span v-else class="form-tip">已发布</span>
          </template>
        </el-table-column>
        <template #empty>暂无更新清单。创建草稿 → 上传 APK → 发布并推送。</template>
      </el-table>
    </el-card>

    <el-dialog v-model="createVisible" title="新建版本草稿" width="480px">
      <el-form :model="createForm" label-width="140px">
        <el-form-item label="版本名">
          <el-input v-model="createForm.versionName" placeholder="如 1.2.0" />
        </el-form-item>
        <el-form-item label="versionCode">
          <el-input-number v-model="createForm.versionCode" :min="1" :step="100" style="width: 100%" />
          <div class="form-tip">v1.2.0 → 10200；必须大于现有最大版本（服务端防降级）</div>
        </el-form-item>
        <el-form-item label="minVersionCode">
          <el-input-number v-model="createForm.minVersionCode" :min="0" :step="100" style="width: 100%" />
          <div class="form-tip">0 = 与 versionCode 相同（仅本版本强制）；10200 = 1.1.x 全量强制更新</div>
        </el-form-item>
        <el-form-item label="更新说明">
          <el-input v-model="createForm.changelog" type="textarea" :rows="4" placeholder="将展示在客户端更新对话框" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createVisible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="handleCreate">创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.updates-page { max-width: 1100px; }
.abi-row { display: flex; align-items: center; gap: 8px; line-height: 24px; }
.abi-name { font-family: monospace; font-size: 12px; min-width: 96px; }
.abi-sha { font-family: monospace; font-size: 12px; color: var(--el-text-color-placeholder); }
.link-like { color: var(--el-color-primary); cursor: pointer; font-size: 12px; }
.upload-btn { cursor: pointer; }
.form-tip { font-size: 12px; color: var(--el-text-color-placeholder); line-height: 1.5; }
</style>
