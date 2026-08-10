// 小趴菜 Web 3.0 — Vue 3 应用入口
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
// 深色模式样式（P3 阶段启用）
// import 'element-plus/theme-chalk/dark/css-vars.css'
import App from './App.vue'
import router from './router'

const app = createApp(App)

// 状态管理
app.use(createPinia())

// 路由
app.use(router)

// UI 框架（Element Plus：蓝色主色 #409EFF，对照 2.0 Material 风格）
app.use(ElementPlus, {
  locale: undefined, // P3 阶段配置中文 locale
})

app.mount('#app')
