"use client";

import { motion, useScroll, useTransform } from "framer-motion";
import Image from "next/image";
import Link from "next/link";
import { useState, useRef, useEffect } from "react";

const FolderIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M2 6C2 4.89543 2.89543 4 4 4H9L11 6H20C21.1046 6 22 6.89543 22 8V18C22 19.1046 21.1046 20 20 20H4C2.89543 20 2 19.1046 2 18V6Z" fill="currentColor" opacity="0.9"/>
  </svg>
);

const BookIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M21 5C21 3.9 20.1 3 19 3H7C5.9 3 5 3.9 5 5V21L12 18L19 21V5ZM19 5L12 7.25L5 5V18.5L12 15.75L19 18.5V5Z" fill="currentColor" opacity="0.9"/>
  </svg>
);

const MapIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M9 2L7.17 4H4C2.9 4 2 4.9 2 6V18C2 19.1 2.9 20 4 20H20C21.1 20 22 19.1 22 18V6C22 4.9 21.1 4 20 4H16.83L15 2H9ZM12 17C9.24 17 7 14.76 7 12C7 9.24 9.24 7 12 7C14.76 7 17 9.24 17 12C17 14.76 14.76 17 12 17Z" fill="currentColor" opacity="0.9"/>
  </svg>
);

const CheckIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M19.5 4.5H4.5C3.67157 4.5 3 5.17157 3 6V19.5C3 20.3284 3.67157 21 4.5 21H19.5C20.3284 21 21 20.3284 21 19.5V6C21 5.17157 20.3284 4.5 19.5 4.5ZM10.6 16.2L6.9 12.5L8.15 11.25L10.6 13.7L16.15 8.15L17.4 9.4L10.6 16.2Z" fill="currentColor" opacity="0.9"/>
  </svg>
);

const MusicIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M18 3V14.5C18 16.43 16.21 18 14 18C11.79 18 10 16.43 10 14.5C10 12.57 11.79 11 14 11C14.73 11 15.42 11.17 16 11.48V7H8V17.5C8 19.43 6.21 21 4 21C1.79 21 0 19.43 0 17.5C0 15.57 1.79 14 4 14C4.73 14 5.42 14.17 6 14.48V3H18Z" fill="currentColor" opacity="0.9"/>
  </svg>
);

function useGitHubStats() {
  const [stars, setStars] = useState(850);
  const [downloads, setDownloads] = useState(12000);

  useEffect(() => {
    const cacheKey = "deskbox_gh_stats_v2";
    const cacheTTL = 24 * 60 * 60 * 1000;

    try {
      const cached = localStorage.getItem(cacheKey);
      if (cached) {
        const { stars: s, downloads: d, ts } = JSON.parse(cached);
        if (Date.now() - ts < cacheTTL) {
          requestAnimationFrame(() => {
            setStars(s);
            setDownloads(d);
          });
          return;
        }
      }
    } catch {}

    Promise.all([
      fetch("https://api.github.com/repos/Tianyu199509/DeskBox")
        .then((r) => r.json())
        .then((d) => d.stargazers_count || 850)
        .catch(() => 850),
      fetch("https://api.github.com/repos/Tianyu199509/DeskBox/releases")
        .then((r) => r.json())
        .then((releases: Array<{ assets: Array<{ download_count: number }> }>) =>
          releases.reduce((sum, r) => sum + r.assets.reduce((s, a) => s + a.download_count, 0), 0)
        )
        .catch(() => 12000),
    ]).then(([s, d]) => {
      setStars(s);
      setDownloads(d);
      try {
        localStorage.setItem(cacheKey, JSON.stringify({ stars: s, downloads: d, ts: Date.now() }));
      } catch {}
    });
  }, []);

  return { stars, downloads };
}

function CharByChar({ text, className, delay = 0 }: { text: string; className?: string; delay?: number }) {
  return (
    <span className={className} aria-label={text}>
      {text.split("").map((char, i) => (
        <motion.span
          key={i}
          initial={{ opacity: 0, y: 16, filter: "blur(4px)" }}
          whileInView={{ opacity: 1, y: 0, filter: "blur(0px)" }}
          viewport={{ once: true, margin: "-40px" }}
          transition={{ duration: 0.45, delay: delay + i * 0.035, ease: [0.16, 1, 0.3, 1] }}
          style={{ display: "inline-block", whiteSpace: char === " " ? "pre" : undefined }}
        >
          {char}
        </motion.span>
      ))}
    </span>
  );
}

function ParallaxImage({ src, alt, width, height, className, style }: { src: string; alt: string; width: number; height: number; className?: string; style?: React.CSSProperties }) {
  const ref = useRef<HTMLDivElement>(null);
  const { scrollYProgress } = useScroll({ target: ref, offset: ["start end", "end start"] });
  const y = useTransform(scrollYProgress, [0, 1], [30, -30]);

  return (
    <div ref={ref} className={className} style={style}>
      <motion.div style={{ y }}>
        <Image src={src} alt={alt} width={width} height={height} className="w-full h-auto" />
      </motion.div>
    </div>
  );
}

function CountUp({ target, suffix = "" }: { target: number; suffix?: string }) {
  const [count, setCount] = useState(0);
  const [started, setStarted] = useState(false);

  return (
    <motion.span
      onViewportEnter={() => {
        if (started) return;
        setStarted(true);
        const duration = 1200;
        const steps = 40;
        const increment = target / steps;
        let current = 0;
        const timer = setInterval(() => {
          current += increment;
          if (current >= target) {
            setCount(target);
            clearInterval(timer);
          } else {
            setCount(Math.floor(current));
          }
        }, duration / steps);
      }}
      viewport={{ once: true, margin: "-50px" }}
    >
      {count.toLocaleString()}{suffix}
    </motion.span>
  );
}

function FAQItem({ question, answer }: { question: string; answer: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="fluent-card" style={{ padding: 0, overflow: "hidden" }}>
      <button
        onClick={() => setOpen(!open)}
        className="w-full flex items-center justify-between p-5 text-left cursor-pointer"
        style={{ background: "transparent", border: "none" }}
      >
        <span className={`font-medium pr-4 transition-colors duration-200 ${open ? "text-[var(--accent)]" : "text-[var(--foreground)]"}`}>{question}</span>
        <motion.svg
          animate={{ rotate: open ? 180 : 0 }}
          transition={{ duration: 0.2 }}
          className="w-5 h-5 flex-shrink-0 text-[var(--secondary)]"
          viewBox="0 0 20 20" fill="currentColor"
        >
          <path fillRule="evenodd" d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z" clipRule="evenodd" />
        </motion.svg>
      </button>
      <motion.div
        initial={false}
        animate={{ height: open ? "auto" : 0, opacity: open ? 1 : 0 }}
        transition={{ duration: 0.25, ease: [0.16, 1, 0.3, 1] }}
        style={{ overflow: "hidden" }}
      >
        <p className="px-5 pb-5 text-[var(--secondary)] text-sm leading-relaxed">{answer}</p>
      </motion.div>
    </div>
  );
}

const scenarios = [
  { Icon: FolderIcon, title: "文件收纳", desc: "把截图、下载内容、临时资料收进真实文件夹，桌面不再堆满图标。" },
  { Icon: MapIcon, title: "常用目录", desc: "把已有项目文件夹映射成桌面格子，不移动原文件，打开和拖拽更直接。" },
  { Icon: BookIcon, title: "随手记录", desc: "保存文本、链接、图片和最近复制内容，临时素材不用在剪贴板里来回找。" },
  { Icon: CheckIcon, title: "桌面轻任务", desc: "把小待办贴在桌面边上，快速输入、勾选完成，也能设置自定义结束时间。" },
  { Icon: MusicIcon, title: "音乐控制", desc: "在桌面格子里控制播放、切换模式、调系统音量，并显示自适应频谱。" },
];

const workflowSteps = [
  { step: "01", title: "下载安装", desc: "下载 DeskBox 1.2.0 安装包，缺少 .NET 8 或 Windows App Runtime 时安装器会提示安装。" },
  { step: "02", title: "创建格子", desc: "创建收纳格子、映射已有文件夹，或开启随记、待办、音乐等功能格子。" },
  { step: "03", title: "放入内容", desc: "拖入文件、保存临时文字和图片、添加轻待办，桌面上的零散内容有了固定位置。" },
  { step: "04", title: "按需唤起", desc: "通过托盘或全局快捷键显示/隐藏格子，也可以按喜好调整主题、字号和标题样式。" },
];

const techHighlights = [
  { icon: "⚡", title: "WinUI 3 原生", desc: "基于 WinUI 3 和 Windows App SDK，尽量贴近 Windows 11 的原生质感。" },
  { icon: "🚀", title: ".NET 8 驱动", desc: "使用 .NET 8 构建，安装器会检测并安装所需运行时依赖。" },
  { icon: "🧩", title: "格子架构重构", desc: "1.2.0 重构了共享外壳、内容宿主、注册表、会话和窗口工厂，后续扩展更稳。" },
  { icon: "🎵", title: "系统媒体会话", desc: "音乐格子接入 Windows 媒体会话，支持播放控制、播放模式和系统音量。" },
  { icon: "🖼️", title: "图片缩略图缓存", desc: "随记最近图片使用缩略图预览，减少大量图片场景下的卡顿和内存压力。" },
  { icon: "🔓", title: "GPLv3 开源", desc: "从 1.2.0 起后续版本使用 GPL-3.0-only，个人用户继续免费开放使用。" },
];

const faqs = [
  { question: "DeskBox 是免费的吗？", answer: "面向个人用户免费开放使用。1.2.0 起后续源码和版本使用 GPL-3.0-only；此前 MIT 版本仍保持原授权。" },
  { question: "支持 Windows 10 吗？", answer: "目前主要推荐 Windows 11。Windows 10 不是完整验证目标，一些视觉效果、系统能力和窗口行为可能会打折。" },
  { question: "安装后需要联网吗？", answer: "应用本身是本地工具。安装时如果缺少 .NET 8 Runtime 或 Windows App Runtime，安装器可以联网下载安装依赖。" },
  { question: "文件放在格子里会被移动吗？", answer: "收纳格子会把文件移动到真实收纳文件夹；文件夹映射只展示已有目录，不移动原文件。" },
  { question: "随记会上传剪贴板或图片吗？", answer: "不会。随记和最近复制内容都保存在本机；图片文字识别使用 Windows OCR 能力。" },
  { question: "音乐格子能替代播放器吗？", answer: "不能。它只负责桌面上的顺手控制，依赖播放器对 Windows 系统媒体控制的支持。" },
];

export default function Home() {
  const { stars, downloads } = useGitHubStats();
  const stats = [
    { value: downloads, suffix: "+", label: "累计下载" },
    { value: stars, suffix: "+", label: "GitHub Stars" },
    { value: 5, suffix: "", label: "格子类型" },
    { value: 23, suffix: " MB", label: "安装包大小" },
  ];
  return (
    <div className="min-h-screen">
      {/* Hero */}
      <section className="relative pt-32 pb-20 px-4 sm:px-6 lg:px-8 overflow-hidden">
        <div className="absolute inset-0 pointer-events-none" style={{ background: "radial-gradient(ellipse 60% 40% at 50% 0%, color-mix(in srgb, var(--accent) 12%, transparent), transparent 70%)" }} />
        <div className="max-w-4xl mx-auto text-center relative">
          <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.6, ease: [0.16, 1, 0.3, 1] }}>
            <div className="flex items-center justify-center gap-3 mb-8">
              <Image src="/deskbox-logo-static.svg" alt="DeskBox" width={48} height={48} />
              <span className="text-3xl font-semibold tracking-tight">DeskBox</span>
            </div>
            <h1 className="text-5xl sm:text-6xl lg:text-7xl font-bold mb-6 leading-[1.05]">
              <CharByChar text="把桌面琐事" delay={0.3} /><br />
              <CharByChar text="收进格子里" delay={0.7} className="text-[var(--accent)]" />
            </h1>
            <motion.p
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.5, delay: 1.2 }}
              className="text-xl text-[var(--secondary)] mb-10 max-w-xl mx-auto leading-relaxed"
            >
              DeskBox 1.2.0 是面向 Windows 11 的轻量桌面整理工具。用文件、随记、待办和音乐格子，把零散内容放回桌面边上。
            </motion.p>
            <motion.div
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.5, delay: 1.4 }}
              className="flex flex-wrap justify-center gap-4"
            >
              <Link href="/download" className="fluent-button fluent-button-primary-shimmer text-lg px-10 py-4">免费下载</Link>
              <Link href="/features" className="fluent-button-secondary text-lg px-10 py-4">了解功能</Link>
            </motion.div>
            <motion.p
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              transition={{ duration: 0.5, delay: 1.6 }}
              className="text-sm text-[var(--secondary)] mt-6"
            >
              <span className="inline-flex items-center px-2.5 py-1 rounded-full bg-[var(--accent-light)] text-[var(--accent)] font-medium text-xs mr-2">v1.2.0</span>
              Windows 11 推荐 · GPLv3 开源
            </motion.p>
          </motion.div>
        </div>
      </section>

      {/* Stats Bar */}
      <section className="py-12 px-4 sm:px-6 lg:px-8 border-y border-[var(--card-border)] bg-[var(--card-background)]/80 backdrop-blur-sm">
        <div className="max-w-4xl mx-auto">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-8">
            {stats.map((s, i) => (
              <motion.div
                key={i}
                initial={{ opacity: 0, y: 10 }}
                whileInView={{ opacity: 1, y: 0 }}
                viewport={{ once: true }}
                transition={{ delay: i * 0.1 }}
                className="text-center relative"
              >
                <div className="absolute inset-0 flex items-center justify-center pointer-events-none">
                  <div className="w-20 h-20 rounded-full bg-[var(--accent)]/5 blur-xl" />
                </div>
                <div className="text-2xl sm:text-3xl font-bold text-[var(--accent)] relative">
                  <CountUp target={s.value} suffix={s.suffix} />
                </div>
                <div className="text-sm text-[var(--secondary)] mt-1 relative">{s.label}</div>
              </motion.div>
            ))}
          </div>
        </div>
      </section>

      {/* Product Screenshots */}
      <section className="px-4 sm:px-6 lg:px-8 pb-24">
        <div className="max-w-5xl mx-auto" style={{ perspective: "1200px" }}>
          <motion.div initial={{ opacity: 0, y: 40, rotateX: 4 }} whileInView={{ opacity: 1, y: 0, rotateX: 0 }} viewport={{ once: true, margin: "-50px" }} transition={{ duration: 0.8, ease: [0.16, 1, 0.3, 1] }} className="relative">
            <div className="absolute inset-0 bg-gradient-to-b from-[var(--accent)]/8 to-transparent rounded-3xl blur-3xl" />
            <ParallaxImage src="/screenshots/product-cover-1280x720.png" alt="DeskBox 界面截图" width={1280} height={720} className="relative rounded-2xl border border-[var(--card-border)] overflow-hidden" style={{ boxShadow: "0 25px 50px -12px rgba(0,0,0,0.15), 0 0 0 1px rgba(0,0,0,0.03)" }} />
          </motion.div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 mt-4">
            <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ delay: 0.1 }}>
              <ParallaxImage src="/screenshots/widget-light.png" alt="DeskBox 浅色模式" width={640} height={400} className="rounded-xl border border-[var(--card-border)] overflow-hidden" style={{ boxShadow: "0 10px 30px -10px rgba(0,0,0,0.1)" }} />
            </motion.div>
            <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ delay: 0.2 }}>
              <ParallaxImage src="/screenshots/widget-dark.png" alt="DeskBox 深色模式" width={640} height={400} className="rounded-xl border border-[var(--card-border)] overflow-hidden" style={{ boxShadow: "0 10px 30px -10px rgba(0,0,0,0.1)" }} />
            </motion.div>
          </div>
        </div>
      </section>

      {/* Workflow Demo */}
      <section className="py-24 px-4 sm:px-6 lg:px-8">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="四步开始整理" /></h2>
            <p className="text-[var(--secondary)] text-lg">从安装到上手，只需要一分钟</p>
          </motion.div>
          <div className="relative">
            <div className="hidden md:block absolute left-1/2 top-0 bottom-0 w-px bg-[var(--card-border)]" />
            <div className="space-y-12 md:space-y-0">
              {workflowSteps.map((step, i) => (
                <motion.div
                  key={i}
                  initial={{ opacity: 0, x: i % 2 === 0 ? -30 : 30 }}
                  whileInView={{ opacity: 1, x: 0 }}
                  viewport={{ once: true, margin: "-50px" }}
                  transition={{ duration: 0.6, delay: i * 0.1 }}
                  className={`relative md:flex items-center ${i % 2 === 0 ? "md:flex-row" : "md:flex-row-reverse"}`}
                >
                  <div className={`md:w-1/2 ${i % 2 === 0 ? "md:pr-12 md:text-right" : "md:pl-12"}`}>
                    <div className="fluent-card">
                      <div className="text-[var(--accent)] font-mono text-sm font-bold mb-2">STEP {step.step}</div>
                      <h3 className="text-lg font-semibold mb-2">{step.title}</h3>
                      <p className="text-[var(--secondary)] text-sm leading-relaxed">{step.desc}</p>
                    </div>
                  </div>
                  <div className="hidden md:flex absolute left-1/2 -translate-x-1/2 w-10 h-10 rounded-full bg-[var(--accent)] text-white items-center justify-center text-sm font-bold z-10 shadow-lg">
                    {step.step}
                  </div>
                  <div className="md:w-1/2" />
                </motion.div>
              ))}
            </div>
          </div>
        </div>
      </section>

      {/* Scenarios */}
      <section className="py-24 px-4 sm:px-6 lg:px-8 bg-[var(--card-background)]">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5 }} className="mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="用在这些场景" /></h2>
            <p className="text-[var(--secondary)] text-lg">不是替代桌面，是帮桌面多一层整理能力</p>
          </motion.div>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            {scenarios.map((s, i) => (
              <motion.div key={i} initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5, delay: i * 0.1 }} className="fluent-card group">
                <div className="w-10 h-10 rounded-lg bg-[var(--accent-light)] flex items-center justify-center mb-4 text-[var(--accent)] group-hover:animate-[float-anim_2s_ease-in-out_infinite]">
                  <s.Icon />
                </div>
                <h3 className="text-lg font-semibold mb-2">{s.title}</h3>
                <p className="text-[var(--secondary)] text-sm leading-relaxed">{s.desc}</p>
              </motion.div>
            ))}
          </div>
        </div>
      </section>

      {/* Tech Highlights - Bento Grid */}
      <section className="py-24 px-4 sm:px-6 lg:px-8">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="技术亮点" /></h2>
            <p className="text-[var(--secondary)] text-lg">为什么 DeskBox 值得选择</p>
          </motion.div>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
            {techHighlights.map((t, i) => (
              <motion.div
                key={i}
                initial={{ opacity: 0, y: 20 }}
                whileInView={{ opacity: 1, y: 0 }}
                viewport={{ once: true }}
                transition={{ delay: i * 0.06 }}
                className="fluent-card group"
              >
                <div className="flex items-start gap-3">
                  <div className="w-10 h-10 rounded-lg bg-[var(--accent-light)] flex items-center justify-center text-xl flex-shrink-0">
                    {t.icon}
                  </div>
                  <div>
                    <h3 className="font-semibold mb-1">{t.title}</h3>
                    <p className="text-[var(--secondary)] text-sm leading-relaxed">{t.desc}</p>
                  </div>
                </div>
              </motion.div>
            ))}
          </div>
        </div>
      </section>

      {/* Core Features - Compact Icon List */}
      <section className="py-24 px-4 sm:px-6 lg:px-8 bg-[var(--card-background)]">
        <div className="max-w-5xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="核心功能" /></h2>
            <p className="text-[var(--secondary)] text-lg">从文件收纳扩展到完整的桌面功能格子</p>
          </motion.div>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-x-12 gap-y-6">
            {[
              { icon: "📦", title: "收纳格子", desc: "拖入文件后移动到真实收纳目录，桌面不再堆满图标" },
              { icon: "📁", title: "文件夹映射", desc: "把已有目录展示到桌面，不移动原文件位置" },
              { icon: "📋", title: "随记", desc: "保存文本、链接、截图和最近复制内容，图片预览更轻" },
              { icon: "✅", title: "待办格子", desc: "快速输入轻任务，支持筛选、编辑和自定义结束时间" },
              { icon: "🎵", title: "音乐格子", desc: "控制播放、切换播放模式、调整系统音量和显示频谱" },
              { icon: "🎨", title: "外观设置", desc: "主题、透明度、文字大小、标题样式和封面氛围可调" },
            ].map((f, i) => (
              <motion.div
                key={i}
                initial={{ opacity: 0, x: i % 2 === 0 ? -16 : 16 }}
                whileInView={{ opacity: 1, x: 0 }}
                viewport={{ once: true }}
                transition={{ delay: i * 0.05 }}
                className="flex items-start gap-4 group"
              >
                <div className="w-10 h-10 rounded-lg bg-[var(--accent-light)] flex items-center justify-center text-lg flex-shrink-0 group-hover:scale-110 transition-transform duration-200">
                  {f.icon}
                </div>
                <div>
                  <h3 className="font-semibold text-[15px] mb-0.5">{f.title}</h3>
                  <p className="text-[var(--secondary)] text-sm leading-relaxed">{f.desc}</p>
                </div>
              </motion.div>
            ))}
          </div>
        </div>
      </section>

      {/* FAQ */}
      <section className="py-24 px-4 sm:px-6 lg:px-8 bg-[var(--card-background)]">
        <div className="max-w-3xl mx-auto">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold mb-3"><CharByChar text="常见问题" /></h2>
            <p className="text-[var(--secondary)] text-lg">关于 DeskBox 的疑问，这里都有答案</p>
          </motion.div>
          <motion.div
            initial={{ opacity: 0, y: 20 }}
            whileInView={{ opacity: 1, y: 0 }}
            viewport={{ once: true }}
            className="space-y-3"
          >
            {faqs.map((faq, i) => (
              <FAQItem key={i} question={faq.question} answer={faq.answer} />
            ))}
          </motion.div>
        </div>
      </section>

      {/* CTA */}
      <section className="py-24 px-4 sm:px-6 lg:px-8">
        <div className="max-w-3xl mx-auto text-center">
          <motion.div initial={{ opacity: 0, y: 20 }} whileInView={{ opacity: 1, y: 0 }} viewport={{ once: true }} transition={{ duration: 0.5 }}>
            <h2 className="text-3xl font-bold mb-3"><CharByChar text="免费下载" /></h2>
            <p className="text-[var(--secondary)] mb-8">23.3 MB · Windows 11 推荐 · 运行时依赖自动安装</p>
            <Link href="/download" className="fluent-button fluent-button-primary-shimmer text-lg px-10 py-4">下载 DeskBox v1.2.0</Link>
          </motion.div>
        </div>
      </section>
    </div>
  );
}
