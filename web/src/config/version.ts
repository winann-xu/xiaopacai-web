// [TASK-MILESTONE-V3] 需求 1：构建产物版本号（vite.config.ts 构建时静态注入）
// 无 tag 的开发构建为 "dev-短哈希"；正式发布为语义化版本（如 "1.1.0"）
declare const __APP_VERSION__: string

/** 当前前端构建版本号（构建时注入，运行时只读） */
export const APP_VERSION: string = __APP_VERSION__
