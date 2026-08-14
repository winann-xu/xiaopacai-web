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
    } catch (e) {
      announcements.value = []
      throw e
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
