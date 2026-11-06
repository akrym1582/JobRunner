/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query: {
      month: string;
      rootPath: string;
    };

    status: 200;
    /** OK */
    resBody: Types.MonthlySchedule;
  };
}>;
