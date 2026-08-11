// 小趴菜 Web 3.0 — 公告管理状态 (Pinia)
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { announcementApi } from '@/api'

export interface Announcement {
  id: number
  title: string
  content: string
  priority: 'normal' | 'important' | 'urgent'
  status: 'draft' | 'published' | 'revoked'
  validUntil: string
  createdAt: string
  publishedAt?: string
}

export const useAnnouncementStore = defineStore('announcements', () => {
  // ---- state ----
  const announcements = ref<Announcement[]>([])
  const loading = ref(false)

  // ---- actions ----
  async function fetchAnnouncements() {
    loading.value = true
    try {
      const res = await announcementApi.list()
      announcements.value = res.data
    } catch {
      // P3 mock
      if (!announcements.value.length) {
        announcements.value = getMockAnnouncements()
      }
    } finally {
      loading.value = false
    }
  }

  async function createAnnouncement(data: Partial<Announcement>) {
    const res = await announcementApi.create(data)
    announcements.value.unshift(res.data)
  }

  async function updateAnnouncement(id: number, data: Partial<Announcement>) {
    const res = await announcementApi.update(id, data)
    const idx = announcements.value.findIndex(a => a.id === id)
    if (idx >= 0) announcements.value[idx] = res.data
  }

  async function deleteAnnouncement(id: number) {
    await announcementApi.delete(id)
    announcements.value = announcements.value.filter(a => a.id !== id)
  }

  async function publishAnnouncement(id: number) {
    await announcementApi.publish(id)
    const a = announcements.value.find(a => a.id === id)
    if (a) {
      a.status = 'published'
      a.publishedAt = new Date().toISOString()
    }
  }

  async function revokeAnnouncement(id: number) {
    await announcementApi.revoke(id)
    const a = announcements.value.find(a => a.id === id)
    if (a) a.status = 'revoked'
  }

  return {
    announcements, loading,
    fetchAnnouncements, createAnnouncement, updateAnnouncement,
    deleteAnnouncement, publishAnnouncement, revokeAnnouncement,
  }
})

// P3 mock
function getMockAnnouncements(): Announcement[] {
  return [
    {
      id: 1, title: '今日使用时长已调整', content: '各位小朋友，今天的屏幕时间已调整为 2 小时。请合理安排学习和娱乐时间。',
      priority: 'normal', status: 'published', validUntil: '2026-08-12T00:00:00Z',
      createdAt: '2026-08-11T08:00:00Z', publishedAt: '2026-08-11T08:00:00Z',
    },
    {
      id: 2, title: '系统维护通知', content: '今晚 22:00-23:00 系统将进行维护更新，期间可能无法正常使用。',
      priority: 'important', status: 'published', validUntil: '2026-08-12T23:00:00Z',
      createdAt: '2026-08-10T20:00:00Z', publishedAt: '2026-08-10T20:00:00Z',
    },
    {
      id: 3, title: '暑假学习计划', content: '暑假期间每日学习类 APP 不限时，鼓励大家多多学习！',
      priority: 'urgent', status: 'draft', validUntil: '2026-09-01T00:00:00Z',
      createdAt: '2026-08-09T12:00:00Z',
    },
  ]
}
