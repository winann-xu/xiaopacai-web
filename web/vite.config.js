import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'path';
import { execSync } from 'child_process';
// [TASK-MILESTONE-V3] 需求 1：构建产物携带版本号（docs/VERSIONING.md）
// 构建时读取 Git tag（如 v1.1.0）；无精确 tag 的开发构建用 dev-短哈希
function gitVersion() {
    try {
        var tag = execSync('git describe --tags --exact-match', { encoding: 'utf8' }).trim();
        if (tag)
            return tag.replace(/^v/, '');
    }
    catch ( /* 无精确 tag（开发构建），走 dev 分支 */_a) { /* 无精确 tag（开发构建），走 dev 分支 */ }
    try {
        return 'dev-' + execSync('git rev-parse --short HEAD', { encoding: 'utf8' }).trim();
    }
    catch (_b) {
        return 'unknown';
    }
}
var appVersion = gitVersion();
// https://vitejs.dev/config/
export default defineConfig({
    plugins: [vue()],
    define: {
        // 全局版本常量：代码中通过 (import.meta as any).env?.__APP_VERSION__ 之外的
        // 静态替换 __APP_VERSION__ 读取（见 src/config/version.ts）
        __APP_VERSION__: JSON.stringify(appVersion),
    },
    resolve: {
        alias: {
            '@': resolve(__dirname, 'src'),
        },
    },
    server: {
        port: 5173,
        proxy: {
            // 开发环境代理后端 API 到 ASP.NET Core
            '/api': {
                target: 'http://127.0.0.1:5000',
                changeOrigin: true,
            },
            '/hubs': {
                target: 'http://127.0.0.1:5000',
                changeOrigin: true,
                ws: true, // SignalR WebSocket
            },
        },
    },
    build: {
        outDir: 'dist',
        assetsDir: 'assets',
        sourcemap: false,
        chunkSizeWarningLimit: 1000,
        rollupOptions: {
            output: {
                // 分包优化：将大型第三方库拆分为独立 chunk，减小首屏体积
                manualChunks: function (id) {
                    // Element Plus UI 组件库 (~1MB)
                    if (id.includes('node_modules/element-plus')) {
                        return 'vendor-element-plus';
                    }
                    // ECharts 图表库 (~500KB)
                    if (id.includes('node_modules/echarts') || id.includes('node_modules/vue-echarts') || id.includes('node_modules/zrender')) {
                        return 'vendor-echarts';
                    }
                    // Vue 核心生态（vue/vue-router/pinia）
                    if (id.includes('node_modules/vue') || id.includes('node_modules/@vue') || id.includes('node_modules/pinia') || id.includes('node_modules/vue-router')) {
                        return 'vendor-vue';
                    }
                    // 通用工具库（axios/dayjs/@microsoft/signalr）
                    if (id.includes('node_modules/axios') || id.includes('node_modules/dayjs') || id.includes('node_modules/@microsoft/signalr')) {
                        return 'vendor-utils';
                    }
                    // 其他 node_modules 归入 vendors
                    if (id.includes('node_modules')) {
                        return 'vendors';
                    }
                },
            },
        },
    },
});
