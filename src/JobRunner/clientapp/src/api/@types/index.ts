/* eslint-disable */
export type Catalog = {
  namespace: string;
  definitionHash: string;
  timeZone: string;
  phase: string;
  networks: Network[];
}

export type DelayMonitorSettings = {
  start?: string | null | undefined;
  startMinutes?: number | null | undefined;
  end?: string | null | undefined;
  endMinutes?: number | null | undefined;
  duration?: boolean | null | undefined;
  durationMinutes?: number | null | undefined;
}

export type Edge = {
  id: string;
  from: string;
  to: string;
  kind: string;
}

export type ExecutionPage = {
  items: Occurrence[];
  total: number;
}

export type Graph = {
  unitPath: string;
  definitionHash: string;
  nodes: Node[];
  edges: Edge[];

  root: Occurrence | null;

  canChange: boolean;
  canRestore: boolean;
}

export type MonthlyEntry = {
  execution: Occurrence;
  status: string;
  executionId: string;
  networkPath: string;
  calendarDate: string;
  comment: string;
  exitCode: number | null;
}

export type MonthlySchedule = {
  month: string;
  timeZone: string;
  units: MonthlyUnit[];
  entries: MonthlyEntry[];
}

export type MonthlyUnit = {
  path: string;
  name: string;
  parent: string;
  type: string;
  comment: string;
  isNetwork: boolean;
}

export type Network = {
  path: string;
  name: string;
  parent: string;
}

export type Node = {
  path: string;
  name: string;
  type: string;
  isNetwork: boolean;
  isRecovery: boolean;
  status: string;

  execution: Occurrence | null;

  exitCode: number | null;
  x: number;
  y: number;
}

export type Occurrence = {
  id?: string | undefined;
  namespace?: string | undefined;
  registrationId?: string | undefined;
  rootId?: string | undefined;
  unitPath?: string | undefined;
  rootPath?: string | undefined;
  rule?: number | undefined;
  businessDate?: string | undefined;
  due?: string | undefined;
  originalDue?: string | undefined;
  state?: string | undefined;
  started?: boolean | undefined;
  attemptId?: string | null | undefined;
  reason?: string | null | undefined;
  finishedAt?: string | null | undefined;
  archivePath?: string | null | undefined;
  jp1ExecutionId?: string | null | undefined;
  materialized?: boolean | null | undefined;
  definitionHash?: string | null | undefined;
  bypassedPredecessors?: string[] | null | undefined;
  standaloneRerun?: boolean | null | undefined;
  scheduleExcluded?: boolean | null | undefined;
  conditionMatched?: boolean | null | undefined;
  statusDetail?: string | null | undefined;
  scheduleChangeId?: string | null | undefined;
  addedByChangeId?: string | null | undefined;
  beforeChangeDue?: string | null | undefined;
  beforeChangeState?: string | null | undefined;
  startedAt?: string | null | undefined;
  restartedAt?: string | null | undefined;
  holdOverride?: boolean | null | undefined;
  beforeChangeHoldOverride?: boolean | null | undefined;
  interruptRequested?: boolean | null | undefined;
  manualExitCode?: number | null | undefined;
  delayMonitor?: DelayMonitorSettings | undefined;
}

export type Operation = {
  stateNamespace?: string | undefined;
  id?: string | undefined;
  kind?: string | undefined;
  target?: string | undefined;
  requestHash?: string | undefined;
  status?: string | undefined;
  result?: string | null | undefined;
  attemptId?: string | null | undefined;
  expectedState?: string | null | undefined;
  reason?: string | null | undefined;
  resolution?: string | null | undefined;
  due?: string | null | undefined;
  registrationKind?: string | undefined;
  end?: string | null | undefined;
  createdAt?: string | null | undefined;
  completedAt?: string | null | undefined;
  archivePath?: string | null | undefined;
  holding?: boolean | null | undefined;
  futureGenerations?: number | null | undefined;
  rerunMethod?: string | null | undefined;
  mutationPath?: string | null | undefined;
  definitionHash?: string | null | undefined;
  expectedDefinitionHash?: string | null | undefined;
  start?: string | null | undefined;
  fixedDue?: string | null | undefined;
  passedDaemonStarts?: string | null | undefined;
  intervalMinutes?: number | null | undefined;
  intervalUntil?: string | null | undefined;
  rerunOptions?: string[] | null | undefined;
  changeLower?: string | null | undefined;
  pushedAhead?: string | null | undefined;
  relativeMinutes?: number | null | undefined;
  dateType?: string | null | undefined;
  errorCode?: string | null | undefined;
  newStatus?: string | null | undefined;
  newReturnCode?: number | null | undefined;
  delayMonitor?: DelayMonitorSettings | undefined;
}

export type ProblemDetails = {
  type?: string | null | undefined;
  title?: string | null | undefined;
  status?: number | null | undefined;
  detail?: string | null | undefined;
  instance?: string | null | undefined;
}
