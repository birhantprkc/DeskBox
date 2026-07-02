"use client";

import { motion } from "framer-motion";
import Image from "next/image";
import { CharByChar } from "@/components/CharByChar";

const features = [
  { title: "收纳格子", subtitle: "把文件真正收进文件夹", description: "创建由真实文件夹支撑的桌面格子。拖入文件后会移动到对应收纳目录，适合整理截图、下载内容、临时资料和项目素材。", image: "/widget-light.png", highlights: ["图标视图 / 列表视图切换", "拖入后进入真实收纳目录", "打开、复制、粘贴、重命名、删除", "统一 hover、空状态和工具提示"] },
  { title: "文件夹映射", subtitle: "不移动文件，原地展示", description: "把已有文件夹映射成桌面格子，保留原目录结构，不复制也不移动文件。常用项目目录、素材目录和工作资料都可以放到桌面边上。", image: "/widget-dark.png", highlights: ["映射任意已有文件夹", "不改变原文件位置", "和收纳格子保持一致操作体验", "支持在资源管理器中显示"] },
  { title: "随记", subtitle: "临时内容盒子", description: "保存文本、链接、截图和最近复制内容。1.2.0 将随记迁移到新的格子体系，并为最近图片预览增加缩略图缓存，图片较多时更顺。", image: "/settings-animation.png", highlights: ["文本、链接、图片本地保存", "最近复制内容可选记录", "图片 OCR 使用 Windows 原生能力", "最近图片缩略图缓存减少卡顿"] },
  { title: "待办格子", subtitle: "桌面上的轻任务", description: "新增独立待办格子，用来记录桌面边上的小事项。支持快速输入、完成状态、筛选、行内编辑、全屏编辑和自定义结束时间。", image: "/settings-appearance.png", highlights: ["本地任务存储", "完成/未完成筛选", "行内编辑和全屏编辑", "自定义结束时间"] },
  { title: "音乐格子", subtitle: "顺手控制正在播放", description: "新增音乐格子，接入 Windows 媒体会话。可以控制播放、切换播放模式、调整系统音量，并显示自适应频谱和封面氛围取色。", image: "/widget-dark.png", highlights: ["播放 / 暂停 / 上一首 / 下一首", "正常 / 随机 / 循环模式切换", "系统音量滑杆", "自适应频谱与封面采样色"] },
  { title: "设置与外观", subtitle: "按新架构重新整理", description: "1.2.0 重新整理设置页，把外观、文件格子、功能格子、交互快捷键、随记和音乐设置放回更清晰的层级。", image: "/settings-appearance.png", highlights: ["文字大小和标题样式", "背景透明度和主题色", "功能格子开关", "全局快捷键和托盘工作流"] },
];

export default function FeaturesPage() {
  return (
    <div className="pt-28 pb-20 px-4 sm:px-6 lg:px-8">
      <div className="max-w-6xl mx-auto">
        <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.6, ease: [0.16, 1, 0.3, 1] }} className="text-center mb-24">
          <h1 className="text-4xl sm:text-5xl font-bold mb-4"><CharByChar text="功能" /></h1>
          <p className="text-[var(--secondary)] text-lg max-w-lg mx-auto">每个功能都围绕一个核心需求设计</p>
        </motion.div>
        <div className="space-y-32">
          {features.map((feature, index) => (
            <motion.div key={index} initial={{ opacity: 0, y: 50 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true, margin: "-80px" }} transition={{ duration: 0.7, ease: [0.16, 1, 0.3, 1] }}
              className={`flex flex-col ${index % 2 === 1 ? "lg:flex-row-reverse" : "lg:flex-row"} gap-16 items-center`}>
              <div className="lg:w-2/5">
                <span className="inline-block text-xs px-2.5 py-1 rounded-full font-medium bg-[var(--accent-light)] text-[var(--accent)] mb-3">{feature.subtitle}</span>
                <h2 className="text-3xl sm:text-4xl font-bold mb-4">{feature.title}</h2>
                <p className="text-[var(--secondary)] text-lg leading-relaxed mb-6">{feature.description}</p>
                <ul className="space-y-3">
                  {feature.highlights.map((h, i) => (
                    <li key={i} className="flex items-center gap-3">
                      <svg className="w-4 h-4 text-emerald-500 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
                        <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                      </svg>
                      <span className="text-[var(--secondary)]">{h}</span>
                    </li>
                  ))}
                </ul>
              </div>
              <div className="lg:w-3/5" style={{ perspective: "800px" }}>
                <div className="feature-image-wrapper rounded-xl border border-[var(--card-border)]" style={{ boxShadow: "0 20px 40px -15px rgba(0,0,0,0.12)" }}>
                  <div className="absolute inset-0 bg-gradient-to-br from-[var(--accent)]/8 to-transparent rounded-2xl blur-3xl" style={{ position: "absolute", zIndex: 0 }} />
                  <Image src={feature.image} alt={feature.title} width={900} height={560} className="relative rounded-xl w-full" style={{ position: "relative", zIndex: 1 }} />
                </div>
              </div>
            </motion.div>
          ))}
        </div>
      </div>
    </div>
  );
}
