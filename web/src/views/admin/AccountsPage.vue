<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：账号管理
import { ref, reactive, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Edit, Delete, Refresh } from '@element-plus/icons-vue'
import { adminAccountApi } from '@/api'

interface Account {
  id: number; username: string; displayName: string; role: 'admin' | 'parent'; email: string; createdAt: string; lastLoginAt?: string
}

// [TASK-PRELAUNCH-P4] 移除 Mock：账号列表走真实 API（GET /admin/accounts），失败显示错误态 + 重试
const accounts = ref<Account[]>([])
const loading = ref(false)
const error = ref<string | null>(null)

async function loadAccounts() {
  loading.value = true
  error.value = null
  try {
    const res = await adminAccountApi.list()
    accounts.value = res.data
  } catch (e: any) {
    error.value = e.response?.data?.message || e.response?.data?.error || '获取账号列表失败'
  } finally {
    loading.value = false
  }
}
onMounted(loadAccounts)

const showEditor = ref(false)
const editingId = ref<number | null>(null)
const editForm = reactive({ username: '', displayName: '', role: 'parent' as 'admin'|'parent', email: '', password: '' })

function openCreate() {
  editingId.value = null
  editForm.username = ''; editForm.displayName = ''; editForm.role = 'parent'; editForm.email = ''; editForm.password = ''
  showEditor.value = true
}

function openEdit(acc: Account) {
  editingId.value = acc.id
  editForm.username = acc.username; editForm.displayName = acc.displayName; editForm.role = acc.role; editForm.email = acc.email; editForm.password = ''
  showEditor.value = true
}

async function saveAccount() {
  if (!editForm.username.trim()) { ElMessage.warning('请输入用户名'); return }
  try {
    if (editingId.value) {
      await adminAccountApi.update(editingId.value, editForm)
      ElMessage.success('账号已更新')
    } else {
      if (!editForm.password) { ElMessage.warning('请输入密码'); return }
      await adminAccountApi.create(editForm)
      ElMessage.success('账号已创建')
    }
    showEditor.value = false
    // [TASK-PRELAUNCH-P4] 增改后重新拉取真实列表（不再本地拼假数据）
    await loadAccounts()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.message || e.response?.data?.error || '操作失败')
  }
}

async function handleDelete(acc: Account) {
  try {
    await ElMessageBox.confirm(`确定删除账号「${acc.username}」？`, '确认删除', { type: 'warning' })
    await adminAccountApi.delete(acc.id)
    await loadAccounts()
    ElMessage.success('已删除')
  } catch { /* */ }
}

async function handleResetPassword(acc: Account) {
  try {
    await ElMessageBox.confirm(`确定重置「${acc.username}」的密码？`, '确认重置', { type: 'warning' })
    await adminAccountApi.resetPassword(acc.id)
    ElMessage.success('密码已重置')
  } catch { /* */ }
}
</script>

<template>
  <div class="admin-page">
    <div class="page-header">
      <h2 class="page-title">账号管理</h2>
      <el-button type="primary" :icon="Plus" @click="openCreate">新建账号</el-button>
    </div>

    <!-- [TASK-PRELAUNCH-P4] 错误态 + 重试（移除 Mock 数据） -->
    <el-alert v-if="error" type="error" :closable="false" style="margin-bottom: 12px">
      <template #title>
        {{ error }}
        <el-button size="small" type="primary" text @click="loadAccounts">重试</el-button>
      </template>
    </el-alert>

    <el-table :data="accounts" v-loading="loading" stripe>
      <el-table-column prop="username" label="用户名" width="140" />
      <el-table-column prop="displayName" label="显示名" width="140" />
      <el-table-column label="角色" width="100">
        <template #default="{ row }">
          <el-tag :type="row.role === 'admin' ? 'danger' : 'primary'" size="small">{{ row.role === 'admin' ? '管理员' : '家长' }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="email" label="邮箱" min-width="200" />
      <el-table-column label="创建时间" width="180">
        <template #default="{ row }">{{ new Date(row.createdAt).toLocaleString('zh-CN') }}</template>
      </el-table-column>
      <el-table-column label="最近登录" width="180">
        <template #default="{ row }">{{ row.lastLoginAt ? new Date(row.lastLoginAt).toLocaleString('zh-CN') : '—' }}</template>
      </el-table-column>
      <el-table-column label="操作" width="240" fixed="right">
        <template #default="{ row }">
          <el-button size="small" :icon="Edit" text type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button size="small" :icon="Refresh" text type="warning" @click="handleResetPassword(row)">重置密码</el-button>
          <el-button size="small" :icon="Delete" text type="danger" @click="handleDelete(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="showEditor" :title="editingId ? '编辑账号' : '新建账号'" width="500px">
      <el-form label-position="top">
        <el-form-item label="用户名" required><el-input v-model="editForm.username" placeholder="登录用户名" /></el-form-item>
        <el-form-item label="显示名"><el-input v-model="editForm.displayName" placeholder="显示名称" /></el-form-item>
        <el-form-item label="角色" required>
          <el-select v-model="editForm.role" style="width:100%">
            <el-option label="家长" value="parent" /><el-option label="管理员" value="admin" />
          </el-select>
        </el-form-item>
        <el-form-item label="邮箱"><el-input v-model="editForm.email" placeholder="邮箱地址" /></el-form-item>
        <el-form-item v-if="!editingId" label="密码" required><el-input v-model="editForm.password" type="password" show-password placeholder="初始密码" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showEditor = false">取消</el-button>
        <el-button type="primary" @click="saveAccount">{{ editingId ? '保存' : '创建' }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.admin-page { max-width: 1200px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
</style>
