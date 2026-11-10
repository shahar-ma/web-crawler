import { useCallback, useEffect, useMemo, useState, type ReactNode, type SubmitEvent } from 'react'
import './App.css'

type JobStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Canceled'
type PageStatus = 'Completed' | 'Failed' | 'Skipped'

type Page = {
  id: string
  url: string
  depth: number
  title: string | null
  status: PageStatus
  error: string | null
  totalLinks: number
  internalLinks: number
  domainLinkRatio: number
  crawledAt: string
}

type Job = {
  id: string
  rootUrl: string
  maxDepth: number
  status: JobStatus
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  failureReason: string | null
  pagesCrawled: number
  pages?: Page[]
}

type CrawlLink = { targetUrl: string; anchorText: string | null; isInternal: boolean }
type IconName = 'grid' | 'globe' | 'pulse' | 'clock' | 'arrow' | 'external' | 'chevron' | 'plus'

const iconContent: Record<IconName, ReactNode> = {
  grid: <><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></>,
  globe: <><circle cx="12" cy="12" r="9" /><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18" /></>,
  pulse: <path d="M3 12h4l3-8 4 16 3-8h4" />,
  clock: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>,
  arrow: <path d="M7 17 17 7M7 7h10v10" />,
  external: <><path d="M14 3h7v7M10 14 21 3" /><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" /></>,
  chevron: <path d="m9 18 6-6-6-6" />,
  plus: <path d="M12 5v14M5 12h14" />,
}

function Icon({ name, size = 18 }: Readonly<{ name: IconName; size?: number }>) {
  return <svg aria-hidden="true" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">{iconContent[name]}</svg>
}

function App() {
  const [jobs, setJobs] = useState<Job[]>([])
  const [selectedJob, setSelectedJob] = useState<Job | null>(null)
  const [url, setUrl] = useState('')
  const [maxDepth, setMaxDepth] = useState(2)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')
  const [expandedPage, setExpandedPage] = useState<string | null>(null)
  const [linkCache, setLinkCache] = useState<Record<string, CrawlLink[]>>({})
  const [loadingLinks, setLoadingLinks] = useState(false)
  const [apiHealthy, setApiHealthy] = useState(false)
  const selectedJobId = selectedJob?.id

  const refresh = useCallback(async () => {
    try {
      const healthResponse = await fetch('/health')
      if (!healthResponse.ok) throw new Error('The API or database health check failed.')
      const response = await fetch('/api/jobs?pageSize=20')
      if (!response.ok) throw new Error('Could not load crawl jobs.')
      const recent: Job[] = await response.json()
      setJobs(recent)
      setApiHealthy(true)
      setError('')
      if (selectedJobId) {
        const detailResponse = await fetch(`/api/jobs/${selectedJobId}`)
        if (detailResponse.ok) setSelectedJob(await detailResponse.json())
      }
    } catch (reason) {
      setApiHealthy(false)
      setError(reason instanceof Error ? reason.message : 'The API is unavailable.')
    }
  }, [selectedJobId])

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0)
    const timer = window.setInterval(() => void refresh(), 2500)
    return () => {
      window.clearTimeout(initial)
      window.clearInterval(timer)
    }
  }, [refresh])

  const stats = useMemo(() => ({
    active: jobs.filter(job => job.status === 'Running' || job.status === 'Queued').length,
    pages: jobs.reduce((sum, job) => sum + job.pagesCrawled, 0),
    completed: jobs.filter(job => job.status === 'Completed').length,
  }), [jobs])

  async function submitJob(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault()
    setSubmitting(true)
    setError('')
    try {
      const response = await fetch('/api/jobs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url, maxDepth }),
      })
      if (!response.ok) {
        const problem = await response.json()
        throw new Error(Object.values(problem.errors ?? {}).flat().join(' ') || problem.title || 'Could not start the crawl.')
      }
      const job: Job = await response.json()
      setSelectedJob(job)
      setExpandedPage(null)
      setLinkCache({})
      setUrl('')
      await refresh()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not start the crawl.')
    } finally {
      setSubmitting(false)
    }
  }

  async function openJob(id: string) {
    try {
      const response = await fetch(`/api/jobs/${id}`)
      if (!response.ok) throw new Error('Could not open this crawl job.')
      setSelectedJob(await response.json())
      setExpandedPage(null)
      setLinkCache({})
      setError('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not open this crawl job.')
    }
  }

  async function cancelJob() {
    if (!selectedJob) return
    try {
      const response = await fetch(`/api/jobs/${selectedJob.id}/cancel`, { method: 'POST' })
      if (!response.ok) throw new Error('Could not cancel this crawl.')
      await openJob(selectedJob.id)
      await refresh()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not cancel this crawl.')
    }
  }

  async function toggleLinks(page: Page) {
    if (expandedPage === page.id) {
      setExpandedPage(null)
      return
    }
    setExpandedPage(page.id)
    if (linkCache[page.id]) return
    setLoadingLinks(true)
    try {
      const response = await fetch(`/api/jobs/${selectedJob?.id}/pages/${page.id}/links?pageSize=100`)
      if (!response.ok) throw new Error('Could not load page links.')
      const links: CrawlLink[] = await response.json()
      setLinkCache(current => ({ ...current, [page.id]: links }))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not load page links.')
    } finally {
      setLoadingLinks(false)
    }
  }

  const progress = selectedJob && ['Completed', 'Failed', 'Canceled'].includes(selectedJob.status)
    ? 100
    : Math.min(100, ((selectedJob?.pagesCrawled ?? 0) / 25) * 100)

  return <DashboardView dashboard={{
    jobs,
    selectedJob,
    url,
    maxDepth,
    submitting,
    error,
    expandedPage,
    linkCache,
    loadingLinks,
    apiHealthy,
    stats,
    progress,
    onNavigateHome: () => setSelectedJob(null),
    onUrlChange: setUrl,
    onDepthChange: setMaxDepth,
    onSubmitJob: submitJob,
    onOpenJob: openJob,
    onCancelJob: cancelJob,
    onToggleLinks: toggleLinks,
    onDismissError: () => setError(''),
  }} />
}

type DashboardViewModel = {
  jobs: Job[]
  selectedJob: Job | null
  url: string
  maxDepth: number
  submitting: boolean
  error: string
  expandedPage: string | null
  linkCache: Record<string, CrawlLink[]>
  loadingLinks: boolean
  apiHealthy: boolean
  stats: { active: number; pages: number; completed: number }
  progress: number
  onNavigateHome: () => void
  onUrlChange: (value: string) => void
  onDepthChange: (depth: number) => void
  onSubmitJob: (event: SubmitEvent<HTMLFormElement>) => void
  onOpenJob: (id: string) => void
  onCancelJob: () => void
  onToggleLinks: (page: Page) => void
  onDismissError: () => void
}

function DashboardView({ dashboard }: Readonly<{ dashboard: DashboardViewModel }>) {
  const { selectedJob, apiHealthy, error, onNavigateHome, onDismissError } = dashboard
  return <div className="app-shell">
    <Sidebar selectedJob={selectedJob} apiHealthy={apiHealthy} onNavigateHome={onNavigateHome} />
    <main className="main-content">
      <TopBar selectedJob={selectedJob} />
      <div className="page-content">
        <WelcomeRow selectedJob={selectedJob} />
        {error && <div className="alert" role="alert"><span>!</span>{error}<button onClick={onDismissError} aria-label="Dismiss error">×</button></div>}
        <DashboardContent dashboard={dashboard} />
        <PageFooter apiHealthy={apiHealthy} />
      </div>
    </main>
  </div>
}

function Sidebar({ selectedJob, apiHealthy, onNavigateHome }: Readonly<{
  selectedJob: Job | null
  apiHealthy: boolean
  onNavigateHome: () => void
}>) {
  return <aside className="sidebar">
    <button type="button" className="brand" onClick={onNavigateHome}>
      <span className="brand-mark"><Icon name="pulse" size={21} /></span>
      <span>url<span className="brand-light">scout</span><small>CRAWL CONTROL</small></span>
    </button>
    <div className="side-label">WORKSPACE</div>
    <nav className="side-nav" aria-label="Main navigation">
      <button className={`nav-item ${selectedJob ? '' : 'active'}`} onClick={onNavigateHome}><Icon name="grid" /> Overview</button>
      <button className="nav-item" onClick={onNavigateHome}><Icon name="globe" /> Crawl jobs</button>
    </nav>
    <div className="sidebar-bottom">
      <div className="system-card">
        <span className={`status-dot ${apiHealthy ? '' : 'offline'}`} />
        <div><strong>System status</strong><small>{apiHealthy ? 'API & database online' : 'Waiting for API connection'}</small></div>
      </div>
      <div className="sidebar-foot">A focused workspace for the open web.<br /><span>URLSCOUT · 1.0</span></div>
    </div>
  </aside>
}

function TopBar({ selectedJob }: Readonly<{ selectedJob: Job | null }>) {
  return <header className="topbar">
    <div className="breadcrumbs"><span>Workspace</span><Icon name="chevron" size={14} /><strong>{selectedJob ? 'Job details' : 'Overview'}</strong></div>
    <div className="topbar-right"><span className="live-indicator"><i /> Live updates</span><span className="avatar">US</span></div>
  </header>
}

function WelcomeRow({ selectedJob }: Readonly<{ selectedJob: Job | null }>) {
  return <section className="welcome-row">
    <div>
      <div className="eyebrow"><span className="eyebrow-line" /> CRAWL CONSOLE</div>
      <h1>{selectedJob ? 'Crawl details' : 'Good crawling starts here.'}</h1>
      <p>{selectedJob ? selectedJob.rootUrl : 'Explore a domain, follow its internal links, and see what connects.'}</p>
    </div>
    <a className="docs-link" href="http://localhost:5080/swagger" target="_blank" rel="noreferrer">API reference <Icon name="external" size={15} /></a>
  </section>
}

function DashboardContent({ dashboard }: Readonly<{ dashboard: DashboardViewModel }>) {
  const { jobs, selectedJob, stats } = dashboard
  return <>
    {!selectedJob && <section className="stats-grid" aria-label="Crawl metrics">
      <StatCard label="Total jobs" value={jobs.length} icon="grid" tone="blue" note="Latest 20 jobs" />
      <StatCard label="Pages discovered" value={stats.pages} icon="globe" tone="mint" note="Across recent jobs" />
      <StatCard label="In progress" value={stats.active} icon="pulse" tone="amber" note="Queued or crawling" />
      <StatCard label="Completed" value={stats.completed} icon="clock" tone="violet" note="Successful crawls" />
    </section>}
    <div className={`dashboard-grid ${selectedJob ? 'detail-layout' : ''}`}>
      {selectedJob ? <JobDetails dashboard={dashboard} job={selectedJob} /> : <OverviewPanels dashboard={dashboard} />}
    </div>
  </>
}

function OverviewPanels({ dashboard }: Readonly<{ dashboard: DashboardViewModel }>) {
  return <>
    <CrawlForm dashboard={dashboard} />
    <RecentJobs jobs={dashboard.jobs} onOpenJob={dashboard.onOpenJob} />
  </>
}

function CrawlForm({ dashboard }: Readonly<{ dashboard: DashboardViewModel }>) {
  const { url, maxDepth, submitting, onSubmitJob, onUrlChange, onDepthChange } = dashboard
  return <section className="panel start-panel">
    <div className="panel-heading">
      <div><span className="panel-kicker">NEW EXPLORATION</span><h2>Start a crawl</h2></div>
      <span className="panel-icon"><Icon name="globe" /></span>
    </div>
    <p className="panel-description">Add a public website to map its pages and connections.</p>
    <form onSubmit={onSubmitJob} className="crawl-form">
      <label htmlFor="url">Website URL</label>
      <div className="input-wrap"><span className="input-prefix">↗</span><input id="url" type="url" required maxLength={2048} placeholder="https://example.com" value={url} onChange={event => onUrlChange(event.target.value)} /></div>
      <label htmlFor="depth">Maximum crawl depth <span className="label-hint">How many clicks to follow</span></label>
      <div className="depth-control">
        {[1, 2, 3].map(depth => <button key={depth} type="button" className={maxDepth === depth ? 'depth-option selected' : 'depth-option'} onClick={() => onDepthChange(depth)}><span>{depth}</span><small>{ShowDepth({ depth })}</small></button>)}
      </div>
      <div className="crawl-note"><span>i</span> Crawls are limited to 25 pages, same-origin links, and public hosts.</div>
      <button type="button" className="primary-button" disabled={submitting}><Icon name="plus" size={17} />{submitting ? 'Starting crawl…' : 'Start crawling'}<Icon name="arrow" size={16} /></button>
    </form>
  </section>
}

function RecentJobs({ jobs, onOpenJob }: Readonly<{ jobs: Job[]; onOpenJob: (id: string) => void }>) {
  return <section className="panel recent-panel" id="jobs">
    <div className="panel-heading">
      <div><span className="panel-kicker">YOUR ACTIVITY</span><h2>Recent crawls</h2></div>
      <span className="recent-count">{jobs.length} {jobs.length === 1 ? 'job' : 'jobs'}</span>
    </div>
    {jobs.length === 0 ? <div className="empty-state"><div className="empty-icon"><Icon name="globe" size={22} /></div><strong>Your crawl history starts here</strong><p>Start your first crawl to see its results and progress.</p></div> :
      <div className="job-list">
        {jobs.map(job => <button className="job-row" key={job.id} onClick={() => onOpenJob(job.id)}>
          <span className="job-favicon"><Icon name="globe" size={17} /></span>
          <span className="job-main"><strong>{trimUrl(job.rootUrl)}</strong><small>{formatDate(job.createdAt)} · {job.pagesCrawled} {job.pagesCrawled === 1 ? 'page' : 'pages'}</small></span>
          <StatusBadge status={job.status} /><Icon name="chevron" size={17} />
        </button>)}
      </div>}
  </section>
}

function JobDetails({ dashboard, job }: Readonly<{ dashboard: DashboardViewModel; job: Job }>) {
  const pages = job.pages ?? []
  const emptyMessage = ShowJobStatus(job.status)
  return <section className="panel results-panel">
    <div className="results-heading">
      <div><span className="panel-kicker">JOB {job.id.slice(0, 8).toUpperCase()}</span><h2>Discovered pages</h2></div>
      <div className="results-actions"><StatusBadge status={job.status} />{['Queued', 'Running'].includes(job.status) && <button className="quiet-button danger" onClick={dashboard.onCancelJob}>Cancel crawl</button>}</div>
    </div>
    <div className="job-overview">
      <div><span>STARTED</span><strong>{job.startedAt ? formatDate(job.startedAt) : 'Waiting for worker'}</strong></div>
      <div><span>DEPTH LIMIT</span><strong>{job.maxDepth} levels</strong></div>
      <div><span>PAGES FOUND</span><strong>{job.pagesCrawled} <small>/ 25</small></strong></div>
      <div className="progress-track"><span style={{ width: `${dashboard.progress}%` }} /></div>
    </div>
    {job.failureReason && <div className="failure-note">{job.failureReason}</div>}
    <PageList pages={pages} expandedPage={dashboard.expandedPage} links={dashboard.linkCache} loadingLinks={dashboard.loadingLinks} onToggle={dashboard.onToggleLinks} />
    {pages.length === 0 && <div className="empty-pages"><span className="loading-orbit" /><strong>{emptyMessage}</strong><p>Results will appear here as the worker processes this job.</p></div>}
  </section>
}

function PageFooter({ apiHealthy }: Readonly<{ apiHealthy: boolean }>) {
  return <footer className="page-footer"><span>URLSCOUT <i>·</i> JOB-BASED WEB CRAWLER</span><span><span className={`footer-dot ${apiHealthy ? '' : 'offline'}`} /> API &amp; SQL {apiHealthy ? 'healthy' : 'unavailable'}</span></footer>
}

function StatCard({ label, value, icon, tone, note }: Readonly<{ label: string; value: number; icon: IconName; tone: string; note: string }>) {
  return <div className="stat-card"><div className={`stat-icon ${tone}`}><Icon name={icon} /></div><span className="stat-label">{label}</span><strong className="stat-value">{value.toLocaleString()}</strong><span className="stat-note">{note}</span></div>
}

function StatusBadge({ status }: Readonly<{ status: JobStatus | PageStatus }>) {
  return <span className={`status-badge ${status.toLowerCase()}`}><i />{status}</span>
}

function ShowDepth({ depth }: Readonly<{ depth: number }>) {
    if (depth === 1)
        return 'Surface';
    return depth === 2 ? 'Balanced' : 'Deep';
}

const ShowJobStatus = (status: JobStatus) => {
    if (status === 'Queued')
        return 'Waiting in queue';
    return status === 'Running' ? 'Finding the first page' : 'No pages discovered';
}
function PageList({ pages, expandedPage, links, loadingLinks, onToggle }: Readonly<{
  pages: Page[]
  expandedPage: string | null
  links: Record<string, CrawlLink[]>
  loadingLinks: boolean
  onToggle: (page: Page) => void
}>) {
  if (pages.length === 0) return null
  return <div className="page-list">
    <div className="page-list-header"><span>PAGE / TITLE</span><span>DEPTH</span><span>LINKS</span><span>DOMAIN RATIO</span><span>STATUS</span></div>
    {pages.map(page => {
      const pageLinks = links[page.id] ?? []
      const showLoadingLinks = loadingLinks && !links[page.id]
      return <div className="page-entry" key={page.id}>
        <div className={`page-row ${expandedPage === page.id ? 'expanded' : ''}`}>
          <div className="page-url-cell" style={{ paddingLeft: 12 + page.depth * 22 }}>
            <span className={`page-node ${page.depth === 0 ? 'root' : ''}`} />
            <span className="page-title-wrap"><strong title={page.url}>{page.title || trimUrl(page.url)}</strong><small title={page.url}>{page.url}</small></span>
          </div>
          <span className="depth-cell">L{page.depth}</span>
          <button className="links-button" onClick={() => onToggle(page)} disabled={page.totalLinks === 0} title="Show discovered links">{page.internalLinks}<span> / {page.totalLinks}</span></button>
          <span className="ratio-cell">{page.domainLinkRatio.toLocaleString(undefined, { style: 'percent', maximumFractionDigits: 0 })}</span>
          <StatusBadge status={page.status} />
        </div>
        {page.error && <div className="page-error">{page.error}</div>}
        {expandedPage === page.id && <div className="link-details">
          {showLoadingLinks && <span>Loading links…</span>}
          {!showLoadingLinks && pageLinks.length === 0 && <span>No links on this page.</span>}
          {pageLinks.length > 0 && pageLinks.map((link, index) =>
            <a key={`${link.targetUrl}-${index}`} href={link.targetUrl} target="_blank" rel="noreferrer" className={link.isInternal ? 'internal-link' : 'external-link'}><span>{link.isInternal ? 'INTERNAL' : 'EXTERNAL'}</span><strong>{link.anchorText || link.targetUrl}</strong><Icon name="external" size={13} /></a>)}
        </div>}
      </div>
    })}
  </div>
}

function formatDate(value: string) {
  return new Date(value).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })
}

function trimUrl(value: string) {
  try {
    const parsed = new URL(value)
    return `${parsed.hostname}${parsed.pathname === '/' ? '' : parsed.pathname}`
  } catch {
    return value
  }
}

export default App
